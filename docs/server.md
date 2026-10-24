# Server

## 1. 실행과 설정

Server는 `Microsoft.NET.Sdk.Web` 기반 ASP.NET Core 9 프로젝트입니다(`src/AI-Ctrl-Agent.Server/AI-Ctrl-Agent.Server.csproj`). 대상 프레임워크는 `net9.0-windows`입니다.

### 설정 로딩

`ServerSetup.BuildAcServer`가 다음 순서로 설정을 구성합니다(`ServerSetup.cs:107-129`).

1. `C:\AiCtrl\AgentServer\config\server.yaml` (없으면 기본값 생성)
2. `C:\AiCtrl\AgentServer\config\server.overrides.yaml` (선택)
3. `AICTRL__` 환경변수

YAML은 `AcYamlConfigurationProvider`가 flat key로 변환합니다. 시퀀스는 `key:0:sub` 형태가 됩니다. 탭과 다중 문서는 거부합니다. 설정 파일은 핫 리로드되지 않습니다.

### 실행 모드

| 모드 | 명령 | 동작 |
| --- | --- | --- |
| 일반 | `AI-Ctrl-Agent.Server.exe` | Kestrel 기동 |
| 시크릿 | `AI-Ctrl-Agent.Server.exe secrets set <name>` | DPAPI 저장소에 값 저장 후 종료 |

시크릿 명령은 호스트를 구성하기 전에 처리됩니다(`Program.cs:4-7`).

```powershell
& "AI-Ctrl-Agent.Server.exe" secrets set google-client-secret
```

입력은 콘솔에서 숨김 처리됩니다. 표준 입력이 리다이렉트되면 한 줄을 읽습니다.

### Kestrel

`kestrel.endpoints` 설정을 그대로 사용합니다(`ServerSetup.cs:129`).

| 이름 | 기본 URL | 용도 |
| --- | --- | --- |
| local | `http://127.0.0.1:5080` | REST, SPA, GeoGate 우회 |
| edge | `http://127.0.0.1:5081` | Cloudflare Tunnel, GeoGate 적용 |

`agentHub.maximumReceiveMessageSize`는 SignalR 최대 수신 크기로 사용됩니다. 기본값은 2MiB입니다.

### 정적 웹

`WebApp.ResolveDistPath`가 `web:distPath`를 우선 사용합니다(`WebApp.cs:14-29`). 설정 경로가 잘못되면 fallback하지 않고 UI를 제공하지 않습니다. dist가 있으면 `GET /`는 `index.html`을 반환하고, SPA의 `HomeRedirect`가 마지막 머신 또는 온라인 머신의 `/m/{machineId}`로 이동합니다. dist가 없을 때만 `GET /`가 `AI-Ctrl-Agent Server` 문자열을 반환합니다.

SPA fallback은 `/api`와 `/hubs`를 제외한 모든 경로에 `index.html`을 반환합니다(`WebApp.cs:47-59`).

## 2. HTTP 엔드포인트

총 69개의 `/api/v1` 엔드포인트와 `GET /`, `GET /health`가 있습니다. 별도 표기가 없으면 `GoogleAuthPolicy.BrowserPolicy`(로그인 쿠키)가 필요합니다.

### 2.1 공통·인증·등록

| 메서드 | 경로 | 인증 | 설명 |
| --- | --- | --- | --- |
| GET | `/health` | 익명 | 버전 반환 |
| GET | `/api/v1/auth/login` | 익명 | Google 로그인 시작, `returnUrl` 지원 |
| GET | `/api/v1/auth/me` | 브라우저 | 현재 사용자와 `authTime` |
| POST | `/api/v1/enroll` | 익명, rate limit | 등록 코드로 머신 등록 |
| POST | `/api/v1/agent/challenge` | 익명, rate limit | 에이전트 challenge 발급 |
| POST | `/api/v1/agent/token` | 익명, rate limit | 서명 검증 후 agent token 발급 |
| POST | `/api/v1/machines/enrollment-codes` | 브라우저 | 등록 코드 생성 (10분) |
| DELETE | `/api/v1/machines/{machineId}` | 브라우저 | 머신 해제 |

### 2.2 머신·세션·조회

| 메서드 | 경로 | 설명 |
| --- | --- | --- |
| GET | `/api/v1/machines` | 머신 목록 |
| GET | `/api/v1/machines/{machineId}` | 머신 상세 |
| GET | `/api/v1/machines/{machineId}/instances` | OpenCode 인스턴스 목록 |
| GET | `/api/v1/machines/{machineId}/sessions` | 세션 목록. `instance` 필수, `view=active|all` |
| GET | `/api/v1/machines/{machineId}/sessions/{sessionId}` | 세션 상세 |
| GET | `/api/v1/machines/{machineId}/sessions/{sessionId}/messages` | 메시지 목록. `limit` 1..200, 기본 50 |
| GET | `/api/v1/machines/{machineId}/sessions/{sessionId}/stream` | 스트림 long-poll. `cursor`, `waitMs` |
| GET | `/api/v1/machines/{machineId}/models` | 모델 목록 |
| GET | `/api/v1/machines/{machineId}/features` | 지원 기능 목록 |
| POST | `/api/v1/machines/{machineId}/instances/refresh` | `instance.refresh` 명령 |

`instance` 쿼리 파라미터가 없으면 400입니다. 메시지 `limit`은 1..200으로 clamp됩니다. 스트림 `waitMs`는 0..`streams.longPollMaxMs`(기본 25,000ms)로 clamp됩니다.

### 2.3 세션 제어

| 메서드 | 경로 | 명령 |
| --- | --- | --- |
| POST | `/api/v1/machines/{machineId}/sessions` | `session.create` (`instance` 필수, body: `directory` 또는 `workspaceId`, `title?`, `initialPrompt?`, `model?`) |
| POST | `/api/v1/machines/{machineId}/sessions/{sessionId}/abort` | `session.abort` |
| POST | `/api/v1/machines/{machineId}/sessions/{sessionId}/prompt` | `session.prompt` |
| POST | `/api/v1/machines/{machineId}/sessions/{sessionId}/abort-and-prompt` | `session.abort_and_prompt` |
| POST | `/api/v1/machines/{machineId}/sessions/{sessionId}/permissions/{requestId}` | `session.permission.reply` |

응답 코드는 명령 상태에 따라 달라집니다. terminal이면 200, pending이면 202, 오프라인이면 503, ack/응답 타임아웃이면 504입니다(`ServerEndpoints.cs:852-932`).

### 2.4 VPN

| 메서드 | 경로 | 설명 |
| --- | --- | --- |
| GET | `/api/v1/machines/{machineId}/vpn/page` | VPN 페이지 스냅샷 |
| GET | `/api/v1/machines/{machineId}/vpn` | VPN 상태 (별칭) |
| GET | `/api/v1/machines/{machineId}/vpn/status` | VPN 상태 |
| GET | `/api/v1/machines/{machineId}/vpn/countries` | 국가 목록 |
| GET | `/api/v1/machines/{machineId}/vpn/servers` | 서버 목록 |
| GET | `/api/v1/machines/{machineId}/vpn/policy` | failover 정책 조회 |
| PUT | `/api/v1/machines/{machineId}/vpn/policy` | `vpn.policy.set` 명령 |
| GET | `/api/v1/machines/{machineId}/vpn/recovery` | 복구·Discord 설정 조회 |
| PUT | `/api/v1/machines/{machineId}/vpn/recovery` | `vpn.recovery.set` 명령 |
| POST | `/api/v1/machines/{machineId}/vpn/connect` | `vpn.connect` 명령 |
| POST | `/api/v1/machines/{machineId}/vpn/disconnect` | `vpn.disconnect` 명령 |

### 2.5 워크스페이스·파일

| 메서드 | 경로 | 설명 |
| --- | --- | --- |
| GET | `/api/v1/machines/{machineId}/workspaces` | 워크스페이스 목록 |
| POST | `/api/v1/machines/{machineId}/workspaces` | 워크스페이스 등록. step-up 필요 |
| GET | `/api/v1/machines/{machineId}/fs` | 드라이브·하위 디렉터리 탐색. `path` 생략 시 드라이브 목록 |
| GET | `.../workspaces/{workspaceId}/files` | 파일 목록 |
| GET | `.../workspaces/{workspaceId}/files/stat` | 파일 메타데이터, `hash` 지원 |
| GET | `.../workspaces/{workspaceId}/files/preview` | 텍스트·이미지 미리보기 |
| POST | `.../workspaces/{workspaceId}/files/mkdir` | `files.mkdir` |
| POST | `.../workspaces/{workspaceId}/files/delete` | `files.delete` |
| POST | `.../workspaces/{workspaceId}/files/rename` | `files.rename` |

레거시 별칭도 함께 매핑되어 있습니다. `.../workspaces/{workspaceId}/stat`, `/preview`, `/mkdir`, `/delete`, `/rename`이 그에 해당합니다(`WorkspaceEndpoints.cs:47-60`).

`/fs`는 워크스페이스에 등록되지 않은 절대 경로도 탐색합니다. `path`가 없으면 드라이브 목록과 자주 쓰는 위치(`quickPaths`: 데스크톱, 문서, 다운로드, 사용자 폴더, AppData)를 반환하고, 있으면 `path`의 하위 디렉터리와 `parentPath`를 반환합니다. 상대 경로나 존재하지 않는 경로는 400 `path_invalid`, hard deny 경로는 403 `path_denied`입니다. 응답의 각 항목에는 `denied`(진입 차단)와 `isReparsePoint`가 포함됩니다.

워크스페이스 등록은 `auth_time`이 `auth.stepUpMinutes`(기본 30분) 이내여야 합니다. 범위를 넘으면 401 `reauth_required`입니다. 다른 파일 변경과 명령에는 별도 step-up이 없습니다.

### 2.6 전송(Transfer)

| 메서드 | 경로 | 인증 | 설명 |
| --- | --- | --- | --- |
| POST | `.../workspaces/{workspaceId}/transfers/uploads` | 브라우저 | 업로드 생성 |
| POST | `.../workspaces/{workspaceId}/transfers/downloads` | 브라우저 | 다운로드 생성 |
| POST | `.../workspaces/{workspaceId}/uploads` | 브라우저 | 레거시 업로드 별칭 |
| POST | `.../workspaces/{workspaceId}/downloads` | 브라우저 | 레거시 다운로드 별칭 |
| GET | `/api/v1/machines/{machineId}/transfers/{transferId}` | 브라우저 | 상태 조회 |
| PUT | `/api/v1/machines/{machineId}/transfers/{transferId}/chunks/{index}` | 브라우저 | 청크 업로드 |
| POST | `/api/v1/machines/{machineId}/transfers/{transferId}/complete` | 브라우저 | 완료 처리 |
| POST | `/api/v1/machines/{machineId}/transfers/{transferId}/dispatch` | 브라우저 | 에이전트 명령 디스패치 |
| GET | `/api/v1/machines/{machineId}/transfers/{transferId}/content` | 브라우저 | 다운로드 콘텐츠 |
| GET | `/api/v1/transfers/{transferId}` | 브라우저 | machineId 없는 상태 조회 |
| PUT | `/api/v1/transfers/{transferId}/chunks/{index}` | 브라우저 | machineId 없는 청크 업로드 |
| POST | `/api/v1/transfers/{transferId}/complete` | 브라우저 | machineId 없는 완료 |
| POST | `/api/v1/transfers/{transferId}/dispatch` | 브라우저 | machineId 없는 디스패치 |
| GET | `/api/v1/transfers/{transferId}/content` | 브라우저 | machineId 없는 콘텐츠 |
| POST | `/api/v1/agent/transfers/{transferId}/begin` | 에이전트 | 다운로드 시작 |
| PUT | `/api/v1/agent/transfers/{transferId}/chunks/{index}` | 에이전트 | 에이전트 청크 업로드 |
| POST | `/api/v1/agent/transfers/{transferId}/complete` | 에이전트 | 에이전트 완료 |
| GET | `/api/v1/agent/transfers/{transferId}/content` | 에이전트 | 업로드 콘텐츠 읽기 |
| GET | `/api/v1/agent/transfers/{transferId}` | 에이전트 | 에이전트 상태 조회 |
| POST | `/api/v1/agent/transfers/{transferId}/release` | 에이전트 | 전송 정리 |

에이전트 전송 경로는 토큰의 machineId와 transfer 소유 machineId가 다르면 404를 반환합니다(`WorkspaceEndpoints.cs:866-884`).

### 2.7 명령·감사·로그

| 메서드 | 경로 | 설명 |
| --- | --- | --- |
| GET | `/api/v1/commands` | 명령 이력 |
| GET | `/api/v1/commands/{commandId}` | 명령 상세와 단계 |
| GET | `/api/v1/audit` | 감사 로그 |
| GET | `/api/v1/logs/server` | 서버 로그 파일 목록 |
| GET | `/api/v1/logs/server/{name}` | 로그 tail. `lines` 1..5000, 기본 500 |

로그 조회는 파일명 traversal을 차단하고 마지막 최대 512KiB에서 tail을 읽습니다(`ServerEndpoints.cs:776-822`, `:980-1003`).

### 2.8 공통 규칙

- `X-Request-Id`가 있으면 26자 ULID 형식이어야 합니다.
- `Idempotency-Key`는 최대 128자입니다.
- `wait` 쿼리는 0..120초로 clamp됩니다.
- 명령 TTL은 생성 시 5분으로 고정됩니다.
- 조회 실패 시 stale 캐시를 반환할 수 있습니다.

## 3. SignalR

### 3.1 `/hubs/agent`

- WebSocket 전용입니다(`ServerSetup.cs:143-146`).
- `AgentTokenDefaults.Policy` 인증을 사용합니다.
- 클라이언트가 호출하는 메서드: `Hello`, `Ack`, `Progress`, `Respond`, `PushEvents`, `PushSnapshot`, `PushStream`.
- 서버가 호출하는 메서드: `Request`, `Cancel`, `RequestSnapshot`, `SetWatchedSessions`.
- 프로토콜 버전은 1입니다. 다른 버전은 `unsupported_protocol`로 거부됩니다.
- `Hello`는 machineId 일치, 머신 존재·폐기 여부, bootId, 마지막 ack seq를 검증·기록합니다. 기존 연결이 있으면 `ForceDisconnect` 후 새 연결을 등록합니다.
- `HelloAck`의 `PendingCommands`는 항상 빈 배열입니다. 클라이언트는 별도로 미보고 명령을 재전송합니다.
- `SnapshotRequired`는 `machine.LastAppliedEventSeq > lastAckedEventSeq`일 때 true입니다.

### 3.2 `/hubs/ui`

- 브라우저 정책을 사용합니다.
- `WatchSession`, `UnwatchSession`을 제공합니다.
- 서버가 호출하는 인터페이스는 7개지만 실제 호출은 `FleetSnapshot`, `VpnChanged`, `StreamChunks`입니다.
- 현재 웹 프론트엔드는 이 허브에 연결하지 않습니다.

## 4. 인증과 보안

### Google OAuth

- `auth.googleClientId`와 시크릿 저장소의 `google-client-secret`이 모두 있어야 스킴이 등록됩니다.
- 콜백 경로: `/signin-google`
- 쿠키: `__Host-aictrl`, `HttpOnly`, `Secure`, `SameSite=Lax`, 12시간, sliding
- 허용 이메일: `auth.allowedEmails`에 포함되고 `email_verified=true`여야 합니다.
- 인증 실패 시 API 경로는 problem+json 401, 비API 경로는 로그인 리다이렉트입니다.
- 로그아웃 엔드포인트는 없습니다.

### 에이전트 인증

1. `POST /api/v1/agent/challenge`로 nonce를 받습니다.
2. 클라이언트가 `aictrl-agent-v1|{machineId}|{nonce}`를 ECDSA P-256으로 서명합니다.
3. `POST /api/v1/agent/token`에 machineId, nonce, 서명을 보냅니다.
4. 서버가 등록된 공개키로 검증하고 단기 토큰을 발급합니다.
5. SignalR 연결은 Bearer 토큰을 사용합니다.

### 등록

- 코드는 `ABCD-EFGH` 형식 8자이며 10분간 유효합니다.
- 서버는 SHA-256 해시만 저장합니다.
- 등록 시 machineId `m_<guid>`와 머신 행을 생성합니다.
- 등록 코드 생성은 로그인 사용자만 가능합니다.
- 등록·challenge·token은 IP당 1분 5회로 제한됩니다.

### Step-up

- 워크스페이스 등록에만 적용됩니다.
- `auth_time` 클레임이 `auth.stepUpMinutes` 이내여야 합니다.
- 그 외 명령·파일 변경에는 적용되지 않습니다.

### GeoGate

- local 포트는 완전히 우회합니다.
- edge 포트는 `CF-Connecting-IP`가 필수입니다.
- 허용 국가는 `geo.allowCountries`(기본 `KR`), CIDR 예외는 `geo.allowCidrs`입니다.
- 국가를 판별할 수 없으면 `geo.failClosed`가 true일 때 403입니다.
- 면제 경로: `/health`, `/api/v1/enroll`, `/api/v1/agent/`, `/hubs/agent`, `/signin-google`
- GeoLite2 데이터베이스 경로는 `geo.dbPath`입니다. 파일이 없으면 edge 판별이 실패합니다.

### 시크릿 저장소

- 경로: `C:\AiCtrl\AgentServer\config\secrets.bin`
- Windows DPAPI `LocalMachine`, entropy `AiCtrl.Server.Secrets.v1`
- `key<TAB>base64(value)` 라인을 암호화해 저장합니다.
- 저장 시 임시 파일 후 원자적으로 교체합니다.
- 서버는 시작 시 값을 읽으므로 변경 후 재시작이 필요합니다.

## 5. 요청 처리

### 조회

`RemoteQueryService.QueryAsync`가 다음을 수행합니다(`RemoteQueryService.cs:53-202`).

1. 머신 존재·폐기 확인
2. `machineId|cacheKey` 기준 in-flight 직렬화
3. 대기 요청은 선행 요청 완료 후 최신 캐시를 재사용
4. 성공 시 `query_cache` 저장
5. 에이전트 실패·오프라인 시 기존 캐시를 stale로 반환
6. 캐시도 없으면 503 또는 504

캐시 키에 payload 전체가 포함되므로 cursor·limit·path 조합마다 별도 캐시가 생성됩니다. in-flight 사전 항목은 제거되지 않습니다.

### 명령

`CommandExecutionService.ExecuteAsync`가 다음을 수행합니다(`CommandExecutionService.cs:49-208`).

1. `Idempotency-Key`로 기존 commandId 조회
2. `X-Request-Id`가 있으면 기존 명령 조회, 없으면 새 ULID 생성
3. `commands` 행 생성, TTL 5분
4. `RequestDispatchService.SendCommandAsync` 호출
5. ack 대기 (기본 10초, 최대 3회)
6. 응답 대기 (타입별 timeout)
7. terminal 결과를 DB에 반영하고 HTTP 상태 결정

| 명령 | 기본 대기 |
| --- | --- |
| `session.abort` | 15초 |
| `session.prompt` | 15초 |
| `session.create` | 20초 (엔드포인트 기본값) |
| `session.abort_and_prompt` | 30초 |
| `vpn.connect` | 120초 |
| `vpn.disconnect` | 30초 |
| 그 외 | `requests.commandSeconds` 기본 30초 |

에이전트가 연결되어 있지 않으면 `requests.waitSeconds`(정수로 읽힘, 기본 10초)만큼 대기한 뒤 503 `machine_offline`을 반환합니다. 기본 YAML의 `requests.waitSeconds`는 매핑이므로 이 값은 기본값으로 fallback됩니다.

## 6. 스트림

`SessionStreamHub`는 세션별 ring buffer와 long-poll 대기자를 관리합니다(`Streams/SessionStreamHub.cs`).

- epoch가 현재보다 작은 batch는 무시합니다.
- epoch가 크면 ring을 교체하고 `LastSeq=0`으로 초기화합니다.
- `seq <= LastSeq`인 chunk는 버립니다.
- ring byte 한도를 넘는 chunk는 경고 후 버립니다.
- 새 chunk가 들어오면 long-poll 대기자를 깨웁니다.
- cursor 형식은 `epoch:seq`입니다.
- cursor가 없으면 현재 스냅샷을 반환합니다.
- cursor가 잘못되었거나 epoch가 다르거나 seq가 미래면 `CursorExpired=true`입니다.
- `seq == LastSeq`이면 새 chunk가 올 때까지 기다립니다.

| 설정 | 기본값 |
| --- | --- |
| `streams.ringChunks` | 2000 |
| `streams.ringBytes` | 1MiB |
| `streams.idleDropMinutes` | 10 |
| `streams.leaseSeconds` | 60 |
| `streams.longPollMaxMs` | 25000 |

Watch lease가 만료되면 에이전트에 watch 해제가 전달됩니다. `SessionWatchCoordinator`는 1초마다 sweep하고 30초마다 heartbeat를 보냅니다. `StreamIdleSweeper`는 1분마다 유휴 스트림을 정리합니다.

## 7. 전송(Transfer)

`TransferStore`가 전송 상태를 관리합니다(`Transfers/TransferStore.cs`).

- ID 접두사: `tr_`
- 기본 디렉터리: `C:\AiCtrl\AgentServer\data\transfers`
- 파일 최대 2GiB, 전체 20GiB, chunk 8MiB, TTL 24시간
- chunk 쓰기는 transfer별 SemaphoreSlim으로 직렬화합니다.
- 예상 chunk 크기와 다르면 400입니다.
- 완료 시 모든 chunk 존재, 크기, SHA-256을 검증합니다.
- 만료된 전송은 `expired`로 전환하고 410을 반환합니다.
- `TransferSweeper`가 10분마다 만료분을 정리합니다.

업로드 완료 시 `files.receive` 명령을 2초 대기로 디스패치합니다. 다운로드 생성 시 `files.send` 명령을 1초 대기로 디스패치합니다. 수동 `/dispatch`는 ready 상태가 아니면 409입니다.

## 8. 저장소

### 테이블

| 테이블 | 주요 내용 |
| --- | --- |
| `users` | 이메일, 이름, 생성 시각 |
| `machines` | 머신 ID, 공개키, 호스트명, OS, 클라이언트 버전, 온라인, 마지막 이벤트 seq |
| `enrollment_codes` | 코드 해시, 만료, 사용 시각 |
| `audit_log` | actor, IP, 액션, 대상, 결과 |
| `commands` | 명령 타입, 상태, payload, 발행자, 시각 |
| `command_steps` | 진행 단계 |
| `idempotency_keys` | 사용자별 멱등 키 → commandId |
| `query_cache` | machineId+key, JSON, asOf |
| `machine_vpn_state` | VPN 상태 JSON, 갱신 시각 |
| `transfers` | 방향, 상태, 크기, chunk, 해시, 만료 |

초기화는 EF `EnsureCreatedAsync`와 raw `CREATE TABLE IF NOT EXISTS`를 함께 사용합니다. migration 파일은 없습니다. 스키마를 바꾸면 양쪽을 함께 수정해야 합니다.

### 감사 로그

등록 성공·코드 생성 등 주요 이벤트를 `audit_log`에 기록합니다. `GET /api/v1/audit`로 조회합니다.

## 9. 로그

- 기본 디렉터리: 실행 파일 디렉터리의 `logs`
- 파일명: `yyyyMMdd-HHmmss-Server.txt`
- 같은 초에 충돌하면 `-{pid}`, `-2`, `-3` 접미사
- 파일 레벨 기본 `Debug`, 콘솔 기본 `Information`
- 보존 30일, 총 5GB, 단일 파일 500MiB
- `AICTRL_LOG_DIR`로 디렉터리 변경 가능
- `GET /api/v1/logs/server`와 `/logs/server/{name}`으로 조회
- authorization, bearer, password, JWT, 등록 코드, `--code`는 redaction 대상입니다.
- ASP.NET Core의 `TraceId`, `RequestId` 등 scope 값은 `{TraceId=..., RequestId=...}` 형태로 파일에 남습니다.

## 10. 설정

전체 키와 실제 소비 여부는 `config-reference.md`를 참조하십시오. 서버에서 실제로 소비되는 주요 키는 다음과 같습니다.

- `kestrel.endpoints.*`
- `auth.googleClientId`, `auth.allowedEmails`, `auth.stepUpMinutes`
- `geo.dbPath`, `geo.allowCountries`, `geo.allowCidrs`, `geo.failClosed`
- `agentHub.maximumReceiveMessageSize`
- `streams.*`
- `transfers.*`
- `logging.*`
- `database.path`
- `web.distPath`
- `requests.ackSeconds`, `requests.commandSeconds`, `requests.querySeconds`, `requests.waitSeconds`(정수), `requests.maxAttempts`

`tunnel`, `forwardedHeaders`, `commands`, `queries`, `notifications`, `backup`는 기본 YAML에 있지만 현재 코드에서 소비처를 확인하지 못했습니다.

## 11. 제한과 주의

- `UseForwardedHeaders`는 배선되어 있지 않습니다. edge 배포 시 클라이언트 IP는 `CF-Connecting-IP` 기반 GeoGate만 사용합니다.
- CORS는 구성되어 있지 않습니다. 웹은 same-origin으로 제공해야 합니다.
- `requests.waitSeconds`는 기본 YAML에서 매핑이므로 정수 오버라이드가 필요합니다.
- `HelloAck.PendingCommands`는 항상 비어 있습니다.
- `AgentHub.Hello`는 `JournalOpenCommandIds`를 사용하지 않습니다.
- UI Hub는 존재하지만 프론트엔드가 연결하지 않습니다.
- 로그 파일은 서버가 실행 중인 디렉터리에 생성되므로 배포 위치를 고정하는 것이 좋습니다.
