# OpenCode 검증 게이트 기록 (M1 — G1·G3·G4·G5)

- 대상: 로컬 설치 OpenCode `1.18.34`
- 실측일: 2026-10-18
- 방법: `opencode serve --hostname 127.0.0.1 --port 14096` (env `OPENCODE_SERVER_USERNAME=opencode`, `OPENCODE_SERVER_PASSWORD=<임시값>`), Basic 인증으로 `GET /global/health`, `GET /doc` 호출
- 산출물: `tests/fixtures/opencode-openapi-1.18.34.json` (478,968 bytes)

## G1. 설치 버전 스키마 — 완료

- `/doc` 저장 완료. openapi `3.1.0`, `info.version` = `1.0.0`, paths 162개.
- 인증: Basic 필수. 미인증 시 401.
- `GET /global/health` → `{ "healthy": true, "version": "<버전 문자열>" }` (200).
- F8 경로 전부 존재 확인: `/session`, `/session/status`, `/session/{sessionID}`, `/session/{sessionID}/message`, `/session/{sessionID}/children`, `/session/{sessionID}/prompt_async`, `/session/{sessionID}/abort`, `/session/{sessionID}/permissions/{permissionID}`, `/event`(`text/event-stream`), `/config/providers`, `/doc`.
- F9 확인: `/api/...`(V2 계열) 경로 병존. Capability probe는 `/doc` 로드 후 경로 존재 검사로 V1/V2 선택.

## G3. `/session/status` 값 종류 — 완료

응답은 `{ "<sessionID>": SessionStatus }` 맵. `SessionStatus`는 3종:

| type | 필드 | 매핑 (`AcRunState`) |
|---|---|---|
| `idle` | 없음 | `Idle` |
| `busy` | 없음 | `Busy` |
| `retry` | `attempt:int`, `message:string`, `next:int`, `action{reason,provider,title,message,label,link}` | `Retrying` (`RetryInfo(Attempt, Message, NextAt)`) |

- `retry.action.reason`은 자유 문자열(enum 아님) → ErrorClassifier 입력으로 사용.
- `WaitingPermission`은 status 값이 아니다. `permission.asked` 이벤트/대기 시간으로 판정(§9.9 규칙 유지).

## G4. quota/rate-limit 오류 JSON — 부분 (스키마 근거 확보, 실캡처 보류)

- 실제 한도 도달 캡처는 보류(쿼터 소진 유발 필요). 발생 시 `tests/fixtures/errors/*.json`에 추가한다.
- 스키마 근거 확보: `APIError{name:"APIError", data{message, statusCode, isRetryable, responseHeaders, responseBody, metadata}}`.
- assistant 메시지 `error` union: `ProviderAuthError`, `UnknownError`, `MessageOutputLengthError`, `MessageAbortedError`, `StructuredOutputError`, `ContextOverflowError`, `ContentFilterError`, `APIError`.
- `session.error` 이벤트 data = `{ sessionID, error(위 union) }`.
- ErrorClassifier 기본 규칙(§9.5 표) 유지 + `retry.action.reason`·`APIError.data.message/responseBody`를 매칭 입력에 포함.

## G5. `prompt_async` messageID 클라이언트 지정 — 부분 (스키마 확인, 중복 실측 보류)

- requestBody에 `messageID`(`pattern ^msg`) 존재 → 클라이언트 지정 허용(멱등키로 사용 가능).
- 동일 messageID 2회 전송 시 중복 생성 여부는 실제 토큰 소모가 필요해 보류 → FakeOpenCode 시나리오로 검증하고, 실 백엔드 실험은 후속.

## 부록. 확인된 응답 스키마 요약

- `/session` 목록 항목(`Session`): `id(^ses)`, `title`, `directory`, `parentID(^ses)?`, `time{created,updated}`, `cost`, `tokens`, `model{id,providerID}`, `version` 등.
- `/session/{id}/message`: `[{ info(Message), parts(Part[]) }]`. `Message` = `UserMessage` | `AssistantMessage`.
  - `UserMessage` 필수: `id`, `sessionID`, `role`, `time`, `agent`, `model`.
  - `AssistantMessage` 필수: `id`, `sessionID`, `role`, `time`, `parentID`, `modelID`, `providerID`, `mode`, `agent`, `path`, `cost`, `tokens`. 선택: `error`, `finish`, `structured`, `summary`, `variant`.
- `Part` 종류: `text`, `subtask`, `reasoning`, `file`, `tool`, `step-start`, `step-finish`, `snapshot`, `patch`, `agent`, `retry`, `compaction`.
  - `TextPart`: `id`, `messageID`, `sessionID`, `text`, `synthetic?`, `time{start?,end?}`.
  - `ToolPart`: `callID`, `id`, `messageID`, `sessionID`, `tool`, `state`(`ToolState`: pending | running | completed | error).
- `POST /session/{id}/abort` → `200 boolean`.
- `POST /session/{id}/permissions/{pid}` body: `{ "response": "once" | "always" | "reject" }`.
- `/event` 이벤트(부분): `message.updated`, `message.part.updated`, `message.part.delta`, `session.idle`, `session.status`, `session.error`, `session.created/updated/deleted`, `permission.asked/replied`, `server.connected` 등. 1.18.x에는 `session.next.*` 계열 이벤트가 추가됨(M5 스트리밍 구현 시 반영 검토).

## 한계와 후속

- G4 실캡처, G5 중복 실측은 실제 쿼터/토큰이 필요해 보류. FakeOpenCode로 경로 검증 후 실환경 재현 시 보강한다.
- `session.next.*`(step/text/reasoning/tool 이벤트 계열)는 Mapper에서 미지 타입으로 무시 가능(관용 파싱). M5에서 소비 검토.

## M1 MUST 테스트 검증 — 완료 (2026-10-18)

FakeOpenCode(경량 가짜 백엔드) 대상 M1 MUST 테스트 작성·통과. 테스트 합계 192개(기존 135 + M1 신규 57), 실패 0, 경고 0.

| 파일 | 개수 | 검증 내용 |
|---|---|---|
| `ErrorClassifierTests` | 20 | 기본 규칙(402/quota, 429, 401/403, 404/503+model, network, aborted), RequireAll, RawJson 입력, ClassifyRetry, 커스텀 룰 우선 |
| `OpenCodeProviderTests` | 20 | Discover(정상/명시 instanceId/미도달 skip/비밀번호 불일치), ListSessions(view 필터·status 병합), GetMessages(페이징·assistant 파싱), Prompt(수락 지연/멱등키/미존재 세션), Abort(정상/첫 1회 무시), Watch(SSE·permission reply·강제 끊김→SnapshotRequired→재연결), ListModels |
| `OpenCodeBackendSupervisorTests` | 11 | adopt, port_in_use(비밀번호 불일치·외부 응답자), managed start+비밀번호 저장, 비밀번호 생성·저장, start_failed, health_timeout+kill, watch 재시작, adopted Unhealthy, Dispose kill/keep |
| `MonitorSystemTests` | 6 | 턴 완료(AiOutput 로그·미로그 파트 flush), stall 판정 1회(TimeProvider 제어), quota retry 분류, session.error 분류(RateLimited), permission.asked, snapshot_required→reconcile |

- §24.1 시나리오(quota retry, abort 첫 1회 무시, SSE 강제 끊김, prompt 수락 지연, Basic 인증 실패)는 FakeOpenCode 시나리오로 검증됨.
- 실행: `dotnet test AI-Ctrl-Agent.sln` (Microsoft.Testing.Platform, `global.json`의 `test.runner` 설정).

## M2 MUST 테스트 검증 — 완료 (2026-10-19)

Server 없이 Client Local API와 stdio ClientMCP만으로 `abort_and_prompt` 실행을 검증했다. 테스트 합계 236개(기존 192 + M2 신규 44), 실패 0, 경고 0.

| 파일 | 개수 | 검증 내용 |
|---|---|---|
| `SessionActorTests` | 3 | 같은 세션 명령 직렬화, `session.abort` 우선 레인, 세션별 독립 실행 |
| `JournalStoreTests` | 5 | command_id 멱등, 상태 전이, 결과 저장, 재시작 시 Running/Acknowledged 복구 |
| `AbortAndPromptRunnerTests` | 7 | Busy abort 후 settle 대기, abort 무시 재시도, `abort_timeout`, `not_settled`, children abort, prompt 확정/미확정(`prompt_unconfirmed`), 재전송 금지 |
| `CommandSystemTests` | 4 | 멱등 조회, 동일 command 동시 실행 합류, 실패 결과 기록, command view 조회 |
| `SessionStateProjectorTests` | 14 | §9.9 우선순위 전 행, Stalled↔Running 전이, QuotaBlocked 해제, WaitingPermission |
| `StateSystemTests` | 1 | 이벤트 버스 수신 후 snapshot 반영 |
| `ClientDbTests` | 1 | machine_id 최초 생성·재사용 |
| `LocalApiSystemTests` | 4 | Bearer/Host 가드, 세션 목록 DisplayState 병합, abort-and-prompt의 CommandSystem 경유, 빈 prompt 거부 |
| `LocalMcpToolsTests` | 4 | 도구 응답 freshness/structuredContent, read-only 조회, `confirm=false` 미리보기(명령 미전송), `confirm=true` 1회 실행 |
| `LocalMcpStdioTests` | 1 | 실제 `AI-Ctrl-Agent.ClientMCP.exe` stdio 프로세스에 initialize → `tools/call` 수행, Server 없이 `Succeeded` 확인 |

### M2 DoD 실행 증거

- `LocalMcpStdioTests.Stdio_client_can_abort_and_prompt_without_server`가 FakeOpenCode 기반 Client 파이프라인에 Local API를 띄우고, 빌드된 ClientMCP 실행 파일을 자식 프로세스로 시작했다.
- 자식 프로세스에는 `AICTRL_LOCAL_PORT`, `AICTRL_LOCAL_TOKEN` 환경변수로 테스트 Local API를 연결했다.
- MCP `initialize` → `notifications/initialized` → `tools/call(local_abort_and_prompt, confirm=true)` 응답의 `structuredContent.data.status`가 `Succeeded`, provider prompt 수신 1회를 확인했다.
- ClientMCP는 `ClearProviders()` 후 파일 로거만 등록해 stdout을 MCP JSON-RPC 전용으로 유지한다.

### M2 한계와 후속

- 관리형 백엔드 비밀번호는 현재 프로세스 메모리에만 유지한다. Client 재시작 후 기존 백엔드 adopt는 M9 DPAPI `ISecretStore` 구현에서 완성한다.
- G4 quota 오류 실캡처와 G5 messageID 중복 실측은 여전히 보류다.
- ProviderHost는 관리형 백엔드 시작·감시·인스턴스 탐색까지 연결했으며, 인스턴스 재탐색 주기(30s)와 프로세스 스캔은 M5 전후로 보강한다.

## M3 MUST 테스트 검증 — 완료 (2026-10-20)

Server 등록·인증·GeoGate·AgentHub와 Client enroll·ServerLink·Outbox를 구현하고, 실제 Kestrel 호스트를 띄우는 통합 테스트로 검증했다. 테스트 합계 263개(기존 236 + M3 신규 27), 실패 0, 경고 0.

| 파일 | 개수 | 검증 내용 |
|---|---|---|
| `GeoGateMiddlewareTests` | 7 | local 리스너 통과, 허용 국가 통과, 차단 국가 403, lookup 실패 fail-closed, 허용 CIDR, `CF-Connecting-IP` 사용·`X-Forwarded-For` 무시, `/hubs/agent` 예외 |
| `EnrollmentAndAuthTests` | 7 | enroll code 형식·정규화·1회 사용·만료 410, challenge/token 발급과 nonce 재사용 거부, 잘못된 서명 거부, Hub Hello 후 outbox 이벤트 seq 중복 제거 |
| `AgentTokenServiceTests` | 4 | 토큰 round-trip, 만료, 변조 거부, 잘못된 형식 거부 |
| `OutboxStoreTests` | 2 | append/ack/seq 조회, 재시작 후 seq 단조 증가 유지 |
| `GoogleAuthPolicyTests` | 4 | 검증된 허용 이메일 통과, 미검증 이메일 거부, 빈 허용 목록 전원 거부, 미허용 이메일 거부 |
| `AuthEndpointTests` | 1 | Google 미설정 시 `GET /api/v1/auth/login` → 503 `auth_unavailable` |
| `ServerLinkSystemTests` | 2 | enroll → 최초 연결(Hello) → outbox flush·ack → 서버 재시작 후 재연결(`ConnectedCount >= 2`), `StopAsync` 중복 호출 무해 |

### M3 DoD 실행 증거

- `ServerLinkSystemTests`는 실제 Kestrel 서버와 SQLite DB를 띄우고, Client `ServerLinkSystem`이 challenge→token→Hub WebSocket→Hello→PushEvents 순서로 동작함을 확인했다. 서버 `LastAppliedEventSeq == 1`, Outbox 잔여 0을 대기 후 검증한다.
- 서버를 같은 포트·같은 DB·같은 secret store로 재시작한 뒤 Client가 재연결(`ConnectedCount >= 2`)하는 것을 15초 내 확인한다. 최초 실패 원인은 테스트가 secret store를 공유하지 않아 재인증이 401로 거부된 것이었고, `InMemoryServerSecretStore`를 두 호스트에 공유해 수정했다.
- GeoGate는 실제 미들웨어 파이프라인이 아니라 단위 테스트로 검증했다. thread-of-execution은 local port 우회 → 예외 경로 → edge port `CF-Connecting-IP` 파싱 → allowCidrs → allowCountries 순서다.
- Rate limit은 enroll/challenge/token에 IP별 fixed window 5/min을 적용했고, 통합 테스트는 rate limit을 우회하도록 서비스를 직접 호출한다.

### M3 한계와 후속

- 실제 Google OIDC 로그인은 clientId/secret이 없어 검증하지 못했다. `/api/v1/auth/login`은 Google 미설정 시 503을 반환하고, 정책 판정은 `GoogleAuthPolicy` 단위 테스트로 대체한다.
- GeoLite2 Country DB가 없어 실제 edge 판정은 fail-closed 403이다. 테스트는 fake `IGeoIpLookup`을 사용한다.
- Client enroll CLI(`enroll --code`)는 프로세스로 직접 실행해 검증하지 않았다. CLI 로직은 enroll → machineId·`server.url` 저장 경로를 사용하며, `ServerLinkSystemTests`가 동일 저장소 경로를 검증한다.
- 관리자 웹 UI, REST `{data, meta}`/`X-Trace-Id` envelope, `SnapshotRequired` 수신 시 스냅샷 처리, 라이브 outbox push(재연결 외 실시간 트리거)는 후속 범위다.

## M4 MUST 테스트 검증 — 완료 (2026-10-20)

Server REST Query/Command end-to-end와 Client 요청 처리·재보고를 구현하고, 실제 Kestrel·SignalR·SQLite를 사용하는 통합 테스트로 검증했다. 테스트 합계 269개(M3 263 + M4 신규 6), 실패 0, 경고 0.

| 파일 | 개수 | 검증 내용 |
|---|---|---|
| `RequestPipelineTests` | 6 | live query와 `meta`/`X-Trace-Id`, 동일 query single-flight, Idempotency-Key command 재사용, offline query stale cache, Command 202 pending·조회, `command.issue` audit 기록 |

### M4 DoD 실행 증거

- `RemoteQueryService`는 `machineId|cacheKey` 기준 `SemaphoreSlim`으로 동일 query를 직렬화한다. 경합 요청은 선행 요청 완료 후 `query_cache.AsOf`가 대기 시작 시각보다 새로우면 재전송 없이 `source=live`, `stale=false`로 응답한다. 통합 테스트에서 동시 요청 2건의 Agent `RequestCount`가 1임을 확인했다.
- Query는 Ack 없이 `Response`를 기다리고, Command는 `Ack` 후 `Response`를 기다린다. Command Ack timeout은 10초·최대 3회 재전송이며, 응답 대기 기본값은 `requests.commandTimeout`(30초)이다. `session.abort`/`session.prompt` 15초, `session.abort_and_prompt` 30초, `vpn.connect` 120초, `vpn.disconnect` 30초를 타입별 기본값으로 적용한다.
- Command가 offline이면 503 `machine_offline`, Ack가 없으면 504 `agent_no_ack`을 반환한다. Query는 offline/timeout 시 `query_cache`가 있으면 `source=cache`, `stale=true`로 200을 반환한다.
- Command/Query requestId는 ULID를 사용하고 `X-Request-Id`로 재사용할 수 있다. `X-Trace-Id` 응답 헤더와 `meta.traceId`를 함께 기록한다.
- `Idempotency-Key`는 사용자·키 기준으로 기존 command를 재사용하며 최대 128자다. 24시간 후 정리 작업은 아직 없다.
- SQLite가 `DateTimeOffset` ORDER BY를 지원하지 않아 `ListAuditAsync`/`ListCommandsAsync`는 `Id` 내림차순으로 정렬한다. `from`/`to` 시간 필터는 아직 통합 테스트로 검증하지 않았다.

### M4 한계와 후속

- `GET /machines`의 `instances`, `workspaces`, `features`, `health`는 DB 기반 빈 값이다. live query 집계는 M5에서 연결한다.
- `GET /machines/{id}/features`는 machine-scoped feature 값이 아니라 `/sessions`, `/commands` 같은 route 목록을 반환한다. 프론트엔드는 키 기반으로 정규화해야 한다.
- Client query handler는 `session.diff`, workspace, client logs를 아직 구현하지 않았다. Server `/logs/server`는 테스트 호스트에서 `AcLog`가 초기화되지 않아 빈 목록을 반환한다.
- 실제 네트워크 차단 후 자동 복구 E2E, `SnapshotRequired` 처리, 라이브 outbox push는 M5 이후 범위다.
- 관리자 웹 UI와 정적 `dist` 서빙은 M5 범위다.

## M5 진행 — 정적 서빙·UiHub·stream (2026-10-20)

`web/ai-ctrl-web`의 Vite 빌드 산출물을 Server가 같은 origin으로 서빙하는 기반, UiHub의 연결/그룹 배선, `SessionStreamHub` ring buffer와 REST long-poll을 추가했다. 테스트 합계 281개(M4 269 + M5 신규 12), 실패 0, 경고 0.

| 파일 | 개수 | 검증 내용 |
|---|---|---|
| `WebAppTests` | 3 | `/`와 SPA 하위 경로가 `index.html` 반환, `/assets/*` 정적 파일 반환, 미매칭 `/api/*`는 index로 fallback하지 않고 404 |
| `UiHubTests` | 3 | 미인증 negotiate는 로그인으로 redirect, 인증 연결 시 `FleetSnapshot` 수신, `WatchSession` 그룹으로 보낸 `StreamChunks` 수신, 미구독 연결은 chunk 미수신 |
| `SessionStreamTests` | 5 | publish/read cursor 왕복, long-poll wake, epoch 변경 시 이전 cursor 만료, stale epoch·중복 seq 무시, REST stream 응답과 `meta.stale` |
| `SessionStreamRingTests` | 1 | ring 크기 제한으로 오래된 chunk 제거, ring 이전 cursor는 `CursorExpired`로 표시 |

- `WebApp.ResolveDistPath`는 `web:distPath` 설정이 있으면 해당 경로를, 없으면 `{contentRoot}/web/ai-ctrl-web/dist`와 `{AppContext.BaseDirectory}/wwwroot` 순서로 `index.html`이 있는 디렉터리를 찾는다.
- `UseAcWebApp`는 `UseDefaultFiles`/`UseStaticFiles`와 SPA fallback을 등록한다. fallback은 `/api`와 `/hubs` 경로를 제외한다.
- `web/ai-ctrl-web`에서 `npm run build`가 성공했고 `dist/index.html`, `dist/assets/*.js`, `dist/assets/*.css`가 생성되는 것을 확인했다.
- `/hubs/ui` UiHub는 쿠키 인증, 연결 직후 `FleetSnapshot`, `WatchSession`/`UnwatchSession` 그룹 배선까지 구현했다. chunk는 `SessionStreamHub.PublishAsync`가 `s:{machineId}:{sessionId}` 그룹으로 broadcast한다.
- `SessionStreamHub`는 cursor `{epoch}:{seq}` 기준으로 ring snapshot/증분을 반환하고, epoch 증가 시 ring을 초기화하며, `Seq` 역행·중복 chunk를 버린다. ring은 2000 chunk/1 MiB, long-poll 최대 25초, idle 10분 후 제거하며 hosted sweeper가 1분마다 정리한다.
- `GET /api/v1/machines/{machineId}/sessions/{sessionId}/stream?cursor=&waitMs=`를 추가했다. cursor 없이 호출하면 ring snapshot, epoch 불일치·ring 이전 cursor는 `cursorExpired=true`와 snapshot을 반환하고 `meta.stale`도 true가 된다.
- `AgentHub.PushStream`은 연결 machine과 batch machine이 다르면 거부하고, 일치하면 `SessionStreamHub`에 publish한다.
- 설정 키는 `streams:ringChunks`, `ringBytes`, `idleDropMinutes`, `longPollMaxMs`, `leaseSeconds`다.
- 아직 StreamSystem(Client), watch lease/`SetWatchedSessions` 집계, Command/Session/Vpn 이벤트 broadcast는 구현하지 않았다. 프론트엔드는 stream 외 데이터를 polling으로 조회한다.
