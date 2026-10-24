# 전체 구조

## 1. 시스템 구성

AI-Ctrl-Agent는 세 개의 실행 단위로 나뉩니다.

```text
┌──────────────────────────────┐
│ 브라우저                      │
│ React SPA (서버 dist 또는 Vite)│
└──────────────┬───────────────┘
               │ REST /api/v1 (Google 쿠키)
               │ SignalR /hubs/ui (현재 프론트 미사용)
               ▼
┌──────────────────────────────┐
│ Server (ASP.NET Core 9)       │
│ - REST, SPA, 인증, 명령 중계   │
│ - SQLite, transfers           │
└──────────────┬───────────────┘
               │ SignalR /hubs/agent (agent token, WebSocket)
               ▼
┌──────────────────────────────┐
│ Agent (Windows 콘솔)           │
│ - OpenCode 관리, 로컬 API      │
│ - VPN, 파일, 전송              │
└──────────────┬───────────────┘
               │ HTTP 127.0.0.1:4096 (Basic auth)
               ▼
┌──────────────────────────────┐
│ OpenCode serve                │
│ - 세션, 메시지, 프롬프트, SSE  │
└──────────────────────────────┘
```

| 구성 요소 | 프로세스 | 주요 책임 |
| --- | --- | --- |
| Web | 브라우저 | 로그인, 머신·세션·파일·VPN·로그 UI |
| Server | `AI-Ctrl-Agent.Server` | Google 인증, 에이전트 연결, 조회 캐시, 명령 디스패치, SQLite 저장 |
| Agent | `AI-Ctrl-Agent.Client` | OpenCode 백엔드, 세션 상태, 스트림, VPN, 파일·전송, 로컬 API |
| Provider | `opencode serve` | 세션 실행과 이벤트 스트림 |
| ClientMCP | `AI-Ctrl-Agent.ClientMCP` | 로컬 API를 감싼 stdio MCP 도구 |

## 2. 실행 토폴로지

### 로컬 개발

- Server local endpoint: `127.0.0.1:5080`
- Server edge endpoint: `127.0.0.1:5081` (사용하지 않아도 열림)
- Vite: `localhost:5173`, `/api`만 5080으로 프록시
- Agent: 로컬 API `127.0.0.1:47800`, OpenCode `127.0.0.1:4096`

### 운영

- Cloudflare Tunnel이 `127.0.0.1:5081`로 연결
- edge 포트는 `CF-Connecting-IP` 헤더를 요구하고 GeoGate를 적용
- 세션 쿠키가 `__Host-` 접두사와 `Secure` 속성을 사용하므로 외부 접속은 HTTPS여야 함
- 웹은 `npm run build` 결과를 서버가 정적 파일로 제공

Server는 `server.yaml`의 `kestrel.endpoints`를 그대로 사용합니다. `launchSettings.json`의 5152는 실제 바인딩에 사용되지 않습니다.

## 3. 서버 내부 구조

### 시작 파이프라인

`ServerSetup.BuildAcServer`(`src/AI-Ctrl-Agent.Server/ServerSetup.cs:107`)가 다음 순서로 구성합니다.

1. `C:\AiCtrl\AgentServer\config\server.yaml` 생성·로드
2. `server.overrides.yaml` 병합 (선택)
3. `AICTRL__` 환경변수 적용
4. 로그 초기화와 Kestrel 구성
5. DI 등록
6. 미들웨어와 엔드포인트 매핑

미들웨어 순서는 다음과 같습니다(`ServerSetup.cs:136-148`).

```text
UseRouting
  → GeoGateMiddleware
  → UseAuthentication
  → UseAuthorization
  → UseRateLimiter
  → UseAcWebApp (정적 SPA + fallback)
  → REST endpoints
  → /hubs/agent, /hubs/ui
```

### 주요 서비스

| 서비스 | 역할 | 근거 |
| --- | --- | --- |
| `ServerDb` | SQLite 접근, 명령·감사·캐시·VPN 상태 | `Persistence/ServerDb.cs` |
| `ConnectionRegistry` | machineId별 에이전트 연결 추적 | `Hubs/ConnectionRegistry.cs` |
| `RequestDispatchService` | 요청 전송, ack 재시도, 응답 대기 | `Requests/RequestDispatchService.cs` |
| `RemoteQueryService` | 조회 직렬화, 캐시 저장, stale fallback | `Requests/RemoteQueryService.cs` |
| `CommandExecutionService` | 명령 생성, 멱등 처리, terminal 대기 | `Requests/CommandExecutionService.cs` |
| `SessionStreamHub` | 스트림 ring, epoch·cursor, long-poll | `Streams/SessionStreamHub.cs` |
| `SessionWatchRegistry` | UI watch lease와 heartbeat | `Streams/SessionWatchRegistry.cs` |
| `TransferStore` | 업로드·다운로드 chunk 저장과 상태 전이 | `Transfers/TransferStore.cs` |
| `ServerStorageSystem` | 서버 측 `IAcSystem` 구현 | `Persistence/ServerStorageSystem.cs` |

백그라운드 hosted service는 `TransferSweeper`, `SessionWatchCoordinator`, `StreamIdleSweeper`입니다. 주기는 각각 10분, 1초 sweep·30초 heartbeat, 1분입니다.

## 4. 에이전트 내부 구조

### 시스템 시작 순서

`AcApp`은 `DependsOn`을 기준으로 위상 정렬한 뒤 `InitAsync` → `StartAsync`를 순서대로 실행합니다(`src/AI-Ctrl-Agent.Core/Runtime/AcApp.cs:49-62`).

```text
provider-host
  → monitor
      → stream
      → state
  → vpn
  → vpn-recovery
  → local-api
  → server-link
  → transfers
```

같은 레벨의 순서는 등록 순서를 따릅니다. `vpn`은 의존성이 없지만 등록 순서상 `state` 이후에 시작합니다.

### 시스템 책임

| 시스템 | 역할 |
| --- | --- |
| `provider-host` | OpenCode 백엔드 기동·감시, 인스턴스 발견 |
| `monitor` | SSE 이벤트 소비, 세션 상태·에러·권한 이벤트 발행 |
| `stream` | 세션 watch, 텍스트·툴·델타 청크 생성 |
| `state` | 세션 상태 투영과 스냅샷 |
| `vpn` | ProtonVPN 상태, 연결·해제, 서버 카탈로그, 정책 |
| `vpn-recovery` | 오류·정지 시 VPN 재연결과 세션 재개 |
| `local-api` | `127.0.0.1:47800` 로컬 HTTP API |
| `server-link` | SignalR 연결, 토큰, outbox, 명령 수신 |
| `transfers` | 업로드·다운로드 재개 |

### 생명주기 규칙

- `InitAsync`는 최대 3회 재시도합니다. 간격은 1초에서 2배씩 증가합니다. 3회 실패 시 프로세스 종료 코드 3으로 종료합니다(`AcApp.cs:8`, `:102-130`).
- `StartAsync`는 재시도하지 않습니다.
- 정지는 시작의 역순이며 시스템별 10초 타임아웃이 적용됩니다(`AcApp.cs:64-90`).
- 시작 중 예외가 나도 이미 시작된 시스템을 되돌리는 롤백은 없습니다.
- `AcLoopSupervisor`는 백그라운드 루프 예외 시 1초에서 60초까지 지수 백오프로 재시작합니다(`AcLoopSupervisor.cs:7-8`, `:97-111`).

## 5. 주요 데이터 흐름

### 5.1 조회(Query)

```text
브라우저 GET /api/v1/machines/{id}/sessions?instance=...
  → ServerEndpoints.QueryAsync
  → RemoteQueryService.QueryAsync
      → 같은 machineId+cacheKey 요청 직렬화
      → RequestDispatchService.SendQueryAsync
      → AgentHub.Request (Kind=Query)
      → ClientRequestHandler.HandleAsync
      → OpenCode HTTP API
      → ResponsePacket
  → query_cache 저장
  → { data, meta } 응답
```

- 캐시 키는 `type + ":" + payload.GetRawText()`입니다(`ServerEndpoints.cs:963`). cursor·limit·path가 다르면 별도 키입니다.
- 에이전트가 오프라인·실패면 기존 캐시를 `meta.stale=true`로 반환합니다(`RemoteQueryService.cs:158-180`).
- 캐시도 없으면 503 `machine_offline` 또는 504 `query_timeout`입니다.
- 기본 조회 타임아웃은 10초입니다(`RequestOptions.cs:11`).

### 5.2 명령(Command)

```text
브라우저 POST .../sessions/{id}/prompt?wait=0
  → CommandExecutionService.ExecuteAsync
      → Idempotency-Key 확인
      → commands 행 생성 (TTL 5분)
      → RequestDispatchService.SendCommandAsync
      → AgentHub.Request (Kind=Command)
      → CommandSystem.ExecuteAsync
          → journal 멱등 기록
          → Ack
          → 실제 작업
          → Progress/Respond
  → terminal: 200 / pending: 202 / offline: 503 / timeout: 504
```

- ack는 최대 3회 재시도, ack 타임아웃 10초입니다(`RequestDispatchService.cs:156-181`).
- ack 후 응답 대기에서 타임아웃이 나면 `Running` 상태로 202를 반환합니다.
- 에이전트는 `CommandId` 기준으로 in-flight 중복을 제거하고, journal에 이미 terminal 결과가 있으면 재생합니다.
- 클라이언트는 `CommandEnvelope.ExpiresAt`를 검사하지 않습니다. 서버 TTL을 넘긴 명령도 실행될 수 있습니다.

### 5.3 이벤트(Event)

- 에이전트 내부 이벤트 버스는 세션 상태 변경, turn 완료, stall, 에러 분류, 권한 요청, 메시지 upsert를 전달합니다.
- 서버로 영속 전송되는 이벤트는 `vpn.state`뿐입니다(`VpnSystem.cs:1132-1167`).
- 서버 outbox는 sequence 기반이며 최대 200건씩 `PushEvents`로 전송합니다. ack 지점까지 삭제합니다.
- 세션·명령 상태는 이벤트 소싱하지 않습니다. 조회와 스트림으로 갱신합니다.

### 5.4 스트림(Stream)

- UI가 세션을 watch하면 서버가 `SessionWatchRegistry`에 lease(기본 60초)를 기록합니다.
- 에이전트는 `SetWatchedSessions`로 watch 목록을 받고 `PushStream`으로 `StreamBatch`를 보냅니다.
- `SessionStreamHub`는 epoch가 바뀌면 ring을 교체하고, `seq <= LastSeq` 청크를 버립니다.
- REST long-poll은 `epoch:seq` 커서를 사용합니다. 커서가 만료되면 `cursorExpired=true` 스냅샷을 반환합니다.
- ring 기본값은 2,000청크/1MiB, long-poll 최대 25초입니다.

### 5.5 전송(Transfer)

```text
업로드: 브라우저 → POST uploads → PUT chunks → complete → dispatch(files.receive) → Agent가 파일 기록
다운로드: POST downloads → files.send 명령 → Agent가 PUT chunks → complete → 브라우저 GET content
```

- 파일 최대 2GiB, 전체 20GiB, chunk 8MiB, TTL 24시간입니다.
- 수신은 `.part` 파일로 재개하고 크기·SHA-256을 검증합니다.
- 현재 클라이언트 재개 루프는 업로드 수신만 처리합니다. 송신 재개는 미구현입니다.

### 5.6 인증과 등록

```text
브라우저 → GET /api/v1/auth/login → Google → /signin-google → __Host-aictrl 쿠키
에이전트 → POST /api/v1/agent/challenge → ECDSA 서명 → POST /api/v1/agent/token → Bearer
등록     → Settings에서 코드 발급(10분) → Client enroll → machines 행 생성
```

- 에이전트 서명 대상은 `aictrl-agent-v1|{machineId}|{nonce}`입니다(`DeviceKeyStore.cs:50`).
- 등록 코드는 SHA-256 해시로 저장되고 1회 사용됩니다.
- 등록 후 에이전트는 재시작해야 서버 링크가 시작됩니다.
- Google 로그인은 `googleClientId`와 `google-client-secret`이 모두 있어야 활성화됩니다.

## 6. 저장소

### 서버 SQLite

`C:\AiCtrl\AgentServer\data\aictrl.db`

| 테이블 | 내용 |
| --- | --- |
| `users` | Google 사용자 |
| `machines` | 등록 머신, 공개키, 온라인 상태, 마지막 이벤트 seq |
| `enrollment_codes` | 코드 해시, 만료, 사용 여부 |
| `audit_log` | 감사 로그 |
| `commands` / `command_steps` | 명령 상태와 단계 |
| `idempotency_keys` | 사용자별 멱등 키 매핑 |
| `query_cache` | 조회 결과 캐시 |
| `machine_vpn_state` | VPN 상태 |
| `transfers` | 전송 메타데이터 |

EF migration은 사용하지 않습니다. `EnsureCreatedAsync`와 `CREATE TABLE IF NOT EXISTS`를 함께 사용합니다.

### 클라이언트 SQLite

`C:\AiCtrl\Agent\data\client.db`

| 테이블 | 내용 |
| --- | --- |
| `kv` | machine_id, enrolled, server.url, outbox seq |
| `journal` | 명령 실행 기록과 결과 |
| `outbox` | 서버로 보낼 이벤트 (vpn.state) |
| `instance_session_seen` | 세션 발견 기록 |
| `transfers` | 진행 중 전송 |

## 7. 보안 경계

| 경계 | 방식 |
| --- | --- |
| 브라우저 → 서버 | Google OIDC, HttpOnly 쿠키, allowedEmails |
| 서버 → 에이전트 | SignalR + agent Bearer token, WebSocket 전용 |
| 에이전트 → OpenCode | HTTP Basic, `secrets.bin`의 `opencode.serve.password` |
| MCP/외부 도구 → 로컬 API | loopback Host 검사 + Bearer local token |
| 파일 접근 | workspace root 하위 정규화, reparse point 최종 대상 검사, deny 패턴 |
| 외부 노출 | GeoGate, Cloudflare edge, IP rate limit, step-up |
| 시크릿 | 서버 DPAPI LocalMachine, 클라이언트 DPAPI CurrentUser |

GeoGate는 로컬 포트(5080)를 완전히 우회합니다. edge 포트(5081)는 `CF-Connecting-IP`가 없으면 403이고, 허용 국가는 기본 `KR`입니다. `/health`, `/api/v1/enroll`, `/api/v1/agent/`, `/hubs/agent`, `/signin-google`은 면제됩니다.

## 8. 설계상 한계

- 웹 프론트엔드는 UI Hub에 연결하지 않습니다. `RealtimeAdapter` 인터페이스만 있고 구현은 polling입니다.
- `ServerMCP`는 빈 Generic Host입니다. MCP 도구는 `ClientMCP`에만 있습니다.
- 세션 상태는 이벤트 소싱이 아닙니다. 서버에 영속되는 이벤트는 VPN 상태뿐입니다.
- 기본 YAML과 코드가 읽는 설정 키가 여러 곳에서 불일치합니다. `config-reference.md`를 참조하십시오.
- Query 6종과 Command 3종은 클라이언트에서 미지원입니다.
- 파일 deny 패턴은 미리보기에서만 차단되고 목록·해시·생성·삭제·이름변경에는 적용되지 않습니다. hard deny 경로는 모든 작업에 적용됩니다.
- 웹 빌드는 CI에 포함되지 않습니다. `dist`는 저장소에 없습니다.

## 9. 관련 문서

- `server.md`: 엔드포인트와 허브 계약
- `client.md`: 에이전트 시스템과 설정
- `web.md`: SPA 구조와 배포
- `operations.md`: 설정·등록·배포 절차
- `config-reference.md`: 설정 키 소비 여부
- `development.md`: 빌드·테스트·결함 목록
