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
- StreamSystem(Client), watch lease/`SetWatchedSessions` 집계는 아래 M5 보강에서 구현했다. Command/Session/Vpn 이벤트 broadcast는 아직 범위가 아니다.

## M5 보강 — watch lease·Client StreamSystem (2026-10-21)

Server의 UI watch 집계와 Client의 `StreamSystem`을 연결해, UI가 구독 중인 세션에 대해서만 Client가 provider 이벤트를 chunk로 전송하도록 구현했다. `message.part.delta` 증분 스트리밍과 full update 중복 방지도 포함한다. 테스트 합계 308개(기존 281 + watch 5 + Client stream 22), 실패 0, 경고 0.

| 파일 | 개수 | 검증 내용 |
|---|---|---|
| `SessionWatchTests` | 5 | REST lease 만료, 만료 직후 재-touch 시 재활성화, UI watch가 REST 만료 뒤에도 유지, UI connection 제거 시 group 해제, Hello 직후 `SetWatchedSessions` 전송과 Unwatch 빈 배열 |
| `StreamSystemTests` | 12 | assistant text Replace/Append/PartDone, `message.part.delta` 증분 스트리밍, full update·delta 중복 방지, user delta 무시, user·미구독 세션 무시, 역할 미확인 text 스트리밍, 재-watch epoch 증가·seq 초기화, lease 만료 차단, `ClearWatches` pending 폐기, 4000자 chunk 분할·복원, tool 상태/완료 1회, 두 세션 batch 분리 |
| `ProviderEventBrokerTests` | 3 | 전체 subscriber 전달, subscriber 예외 격리, 구독 해제 |
| `AgentPushChannelTests` | 5 | 미연결 false, attach 전송, sender 예외 false, 일치하는 sender만 detach, 호출자 취소 전파 |
| `MonitorSystemTests` | 7 | 기존 6개 + provider 이벤트 broker publish |
| `ServerLinkSystemTests` | 3 | 기존 2개 + UI watch → `SetWatchedSessions` → Client `StreamSystem` → `PushStream` → Server ring end-to-end |

- Server `SessionWatchRegistry`는 `(machineId, sessionId)` 단위로 UI connection과 REST lease를 함께 저장한다. `SessionWatchCoordinator`는 1초마다 만료를 정리하고 30초 heartbeat로 `SetWatchedSessions`를 재전송하며, watch 변경·해제 시 즉시 같은 payload를 보낸다.
- `UiHub.WatchSession`/`UnwatchSession`과 `OnDisconnectedAsync`, REST stream 조회의 `TouchRestAsync`가 watch 상태를 갱신한다. `AgentHub.Hello`는 연결 직후 해당 connection에 현재 watch 목록을 보낸다.
- Client `ProviderEventBroker`는 `MonitorSystem`이 provider 이벤트를 성공적으로 처리한 뒤 publish한다. `StreamSystem`은 자기 machine과 유효한 sessionId만 watch하고, assistant text는 첫 chunk를 `Replace`, prefix 확장은 `Append`, 종료 시 `PartDone`으로 보낸다. reasoning/tool도 동일한 part 단위 규칙을 사용한다.
- OpenCode 1.18.34는 초기 `message.part.updated`, 스트리밍 중 `message.part.delta`(`field: "text"`), 종료 시 누적 text를 담은 `message.part.updated`를 보낸다. `StreamSystem`은 delta를 `Append`로 반영하고, 직전 full update에 이미 포함된 첫 delta는 접미사 일치로 1회 건너뛰며, delta 구간의 중간 full text update는 중복 방지를 위해 무시한다. 종료 full update는 누적 text를 재조정한 뒤 `PartDone`을 보낸다.
- `StreamSystem` epoch는 Client 시각(Unix ms)을 기준으로 시작해 프로세스 재시작 후에도 Server의 이전 epoch보다 크게 유지한다. 재-watch/lease 만료 후 재개 시 epoch를 증가시키고 seq를 1부터 다시 센다.
- text는 UTF-8 기준 안전을 위해 4000자 단위로 나누며 surrogate pair 경계를 보정한다. flush는 100ms 주기로 session별 `StreamBatch`를 만들고 `AgentPushChannel`로 `PushStream`을 호출한다.
- `ServerLinkSystem`은 `SetWatchedSessions` 수신, 연결 후 push sender attach, 연결 종료 시 detach·`ClearWatches`를 담당한다. push는 기존 방침대로 best-effort이며 Outbox를 거치지 않는다.
- 검증: `dotnet build AI-Ctrl-Agent.sln --nologo -v:q` 경고 0/오류 0, `dotnet test AI-Ctrl-Agent.sln --nologo --no-build` 308/308 통과. `ServerLinkSystemTests`의 end-to-end 테스트는 실제 Kestrel·SignalR·SQLite 환경에서 watch lease가 Client까지 도달한 뒤 provider text가 Server ring에 `Replace` chunk로 저장되는 것을 확인한다.

## M5 프론트엔드 — Files·VPN 화면 (2026-10-23)

`web/ai-ctrl-web`의 Files/VPN placeholder를 실제 화면으로 교체하고, M4 파일·전송·VPN API와 연결했다. MSW fixture/handler에 파일 트리, 전송 상태, VPN 국가/도시/서버 스냅샷을 추가해 백엔드 없이 전체 흐름을 검증할 수 있다. 테스트 합계는 프론트엔드 27개, 백엔드 424개(423 통과·1 skip)이며 실패는 없다.

| 파일 | 개수 | 검증 내용 |
|---|---|---|
| `app.integration.test.tsx` | 13 | 파일 목록 탐색·텍스트 미리보기, 폴더 생성과 pending 명령 접수, 파일 업로드와 목록 반영, VPN 연결 해제·재연결 명령 추적 |
| 기존 프론트엔드 테스트 | 14 | 세션 상태·정렬·트리, 신선도 유틸리티, 인증/오류 상태, 명령 이력, 기능 경로 |
| `dotnet test` | 424 | 기존 백엔드 계약·전송·VPN·스트림 회귀 (423 통과, 1 skip) |

- Files 화면은 워크스페이스 선택, breadcrumb, 정렬, cursor pagination, 텍스트/이미지/바이너리 미리보기, 업로드(충돌 정책), 다운로드(ZIP 포함), 권한 기반 mkdir/rename/delete를 제공한다. URL의 `ws`, `path`, `sort`, `preview`로 상태를 유지한다.
- 파일 목록은 20초 polling, 변경 명령은 즉시 무효화 후 1.5초 뒤 재무효화한다. 업로드는 chunk 전송 후 `complete`를 호출하고 최대 약 10초간 전송 상태를 확인하며, 다운로드는 `ready`까지 최대 약 60초 대기한다.
- VPN 화면은 상태·만료·출구 IP, 국가/도시 필터, 서버 부하·점수·최근 연결, 자동/수동 서버 선택, `force` 연결, 연결 해제를 제공한다. 연결/해제 명령은 1초 polling으로 최대 60초 추적하고, 처리 중에는 관련 버튼을 비활성화한다.
- MSW는 파일 변경·전송·VPN 명령을 pending command로 재현한다. VPN 명령 완료 시 busy 상태를 해제하고, 파일 변경은 목록 무효화로 반영한다.
- 검증: `npm run typecheck` 오류 0, `npm run lint` 오류 0(기존 경고 3), `npm test` 27/27 통과, `npm run build` 성공, `dotnet test` 423/424 통과(1 skip).

## M6 실제 OpenCode·Client E2E (2026-10-23)

실제 OpenCode `1.18.34`와 Client를 재시작한 뒤 Local API에서 명령을 보내 세션 프롬프트와 응답 수신까지 확인했다. 서버는 `127.0.0.1:5080`, Client Local API는 `127.0.0.1:47800`, OpenCode는 `127.0.0.1:4096`, machineId는 `m_ff1bacd596e84c3e8e0a667958ce50c2`다.

| 항목 | 결과 |
|---|---|
| `prompt` command | `93557f3281e244ab9a80b914e88a99df` → `msg_93557f3281e244ab9a80b914e88a99df` |
| `prompt` command (재시작 후) | `b08db5f7d74643bc973be555cead5c4e` → `msg_b08db5f7d74643bc973be555cead5c4e` |
| 세션 | `ses_e994ebad7ffeK4iuAxTy7UPSVc` (`AICtrl E2E local API`) |
| 모델 | `opencode/deepseek-v4.1-flash-free` |
| 응답 | `AICTRL_E2E_RESTART_OK` |
| Client 상태 | `serverConnected=true`, `instanceCount=1` |
| 관리 실행 | `opencode` → `C:\Users\<user>\AppData\Roaming\npm\opencode.cmd` → `cmd.exe /d /s /c` → `opencode.exe serve` |

- `prompt_async`의 `messageID`는 `msg`로 시작해야 한다. 기존 Client는 `CommandSystem`의 `CommandId`를 그대로 넘겨 400을 받았고, `OpenCodeInstance`에서 `msg_` 접두사를 보정하도록 수정했다. Fake 서버도 같은 규칙으로 400을 반환해 회귀를 재현한다.
- `OpenCodeProviderTests.Prompt_normalizes_command_id_to_message_id`를 추가했다. 전체 `dotnet test`는 `423 통과, 1 skip, 424 total`, 빌드는 경고 0/오류 0이다.
- 관리 실행은 `AICTRL__opencode__executable` 환경 변수 없이 재시작해 검증했다. `ProcessBackendController`가 Windows npm shim(`.cmd`)을 찾아 `cmd.exe /d /s /c`로 실행하고, OpenCode 자식 프로세스가 `4096`에서 listen하는 것을 확인했다.
- 기본 모델 `anthropic/claude-sonnet-4-6`은 `ANTHROPIC_BASE_URL=http://localhost:8082`를 상속하지만 해당 로컬 게이트웨이가 없어 `Cannot connect to API`로 실패한다. 위 E2E는 Local API `model` override로 `opencode/deepseek-v4.1-flash-free`를 지정해 검증했다. 서버/Client 설정을 바꾸지 않았다.
- VPN은 `NL#606` 연결 상태를 유지했고, 이번 E2E에서 disconnect/connect를 수행하지 않았다. 따라서 `protonvpn-auto` 서버 전환과 split tunneling 경로는 아직 실측하지 않았다.
- Local API `/status`의 `serverConnected`는 `ServerLinkPresence`를 반영하도록 수정했다. `sessionCount`는 재시작 직후 이벤트가 없을 때 `0`으로 남을 수 있으므로 UI는 `/sessions` 목록을 기준으로 사용해야 한다.
- 실제 Google 로그인 기반 UI E2E는 아직 수행하지 않았다. 테스트용 인증 우회는 추가하지 않는다.

## M7 protonvpn-auto 상태 연동 (2026-10-23)

`protonvpn-auto.exe`를 VPN 백엔드로 사용할 수 있도록 `ProtonVpnCli`와 `VpnSystem`을 확장했다. 기존 Bun CLI와 응답 스키마가 달라 상태·서버·연결 결과 파서를 분리했고, 실제 Client에서 `status --json`을 읽어 Server DB `machine_vpn_state`까지 전달되는 것을 확인했다.

| 항목 | 결과 |
|---|---|
| 실행 파일 | `E:\_Dev\protonvpn-auto\protonvpn-auto.exe` |
| 명령 | `status --json`, `servers {code} --json --limit 2000`, `connect {server} --json --timeout {seconds}`, `disconnect --json --timeout {seconds}` |
| 감지 | `PROTONVPN_AUTO` 또는 실행 파일명 `protonvpn-auto` |
| helper | auto 모드에서는 `HelperVpnProcessRunner`를 우회하고 로컬 프로세스로 실행 |
| GUI | `ProtonVPN.Client` 프로세스를 GUI로 감지하고, auto 모드에서는 동시 실행 경고를 표시하지 않음 |
| 실시간 상태 | `JP#277`, country `JP`, city `도쿄도`, exitIp `159.26.119.36` |
| 상태 플래그 | `usesProtonApp=true`, `protonGuiRunning=true`, `helperAvailable=false`, `signedIn=true`, `username=null` |
| 표시 문구 | `Proton VPN 앱 · 터널 연결됨 (JP#277)` |
| 계정 표시 | auto 모드에서 username이 없으면 `Proton VPN 앱`으로 표시 |

- auto connect 응답은 `connected`가 boolean이고, 기존 Bun CLI는 `connected`가 객체다. 두 형태를 모두 파싱하며 auto 서버 ID는 정규식 `[A-Za-z]{2}#\d+`로 추출한다.
- auto 서버 목록의 `tier`는 문자열(`FREE`/`BASIC`/`PLUS`/`UNLIMITED`)이므로 숫자 등급으로 매핑하고, 국가명은 응답 루트의 `country`를 fallback으로 사용한다.
- `VpnStatusView.UsesProtonApp`을 추가하고, auto 모드에서는 helper/GUI 충돌 경고를 숨기며 계정 미확인 상태를 `로그인 안 됨`으로 오표시하지 않도록 수정했다.
- Vitest가 `e2e/*.spec.ts`를 수집해 Playwright와 충돌하던 문제를 `test.include = src/**/*.{test,spec}.{ts,tsx}`로 분리했다.
- 검증: `npm test` 28/28 통과, `npm run typecheck` 오류 0, `npm run build` 성공, `dotnet build` 경고 0/오류 0, `dotnet test` 429 통과·1 skip·430 total.
- 관찰 중 VPN 서버가 `NL#853` → `LT#33` → `JP#277`로 변경됐다. Client가 connect/disconnect를 실행한 것은 아니며, 실제 서버 전환과 split tunneling 경로는 여전히 미실측이다.

## M8 API 인증 challenge 정리 (2026-10-23)

실제 SPA를 `http://127.0.0.1:5080/index.html`로 열었을 때 `/api/v1/auth/me`가 Google OAuth로 302 redirect되고, 브라우저 fetch가 CORS 오류로 실패해 로그인 화면 대신 `서버에 연결할 수 없습니다`가 표시됐다. 쿠키 인증의 기본 challenge가 API 요청에도 로그인 redirect를 반환한 것이 원인이었다.

- `AuthenticationSetup`의 쿠키 이벤트에서 `/api` 경로는 redirect 대신 `401`/`403` `application/problem+json`을 반환하도록 수정했다. `/hubs/*`와 일반 경로는 기존 redirect 동작을 유지한다.
- `AcErrorCodes.Forbidden`을 추가하고 access denied 응답에 사용한다.
- 회귀 테스트 `AuthEndpointTests.Api_returns_401_problem_for_unauthenticated_requests`가 `/api/v1/auth/me`와 `/api/v1/machines`의 401 problem 응답을 검증한다.
- 검증: `dotnet build` 경고 0/오류 0, `dotnet test` 429 통과·1 skip·430 total. 실제 서버에서 두 경로 모두 `401 Unauthorized` + `application/problem+json`을 반환했고, 브라우저는 `/index.html`에서 `/login`으로 이동해 `원격 제어 콘솔`과 `Google로 로그인` 링크를 표시했다.
- 실제 Google 계정 로그인과 로그인 후 UI E2E는 아직 수행하지 않았다. `__Host-aictrl` 쿠키의 `SecurePolicy.Always`가 로컬 HTTP에서 어떻게 동작하는지도 로그인 후 확인해야 한다. 테스트용 인증 우회는 추가하지 않는다.

## M9 VPN failover policy (2026-10-23)

OpenCode 할당량 소진·응답 시간 초과 시 현재 턴을 중단하고, 정책에 맞는 ProtonVPN 서버로 전환한 뒤 같은 세션을 재개하는 failover 경로를 구현했다. 정책은 Client가 소유하고 UI는 Server API를 통해 조회·수정한다. 백엔드 테스트는 `447 통과·1 skip·448 total`, 프론트엔드 테스트는 `29/29 통과`다. 실제 VPN disconnect/connect와 실제 OpenCode E2E는 수행하지 않았다.

| 파일 | 검증 내용 |
|---|---|
| `VpnPolicyTests` | 정책 기본값·정규화·원자적 저장, 손상 파일 fallback, 후보 필터·정렬, 쿨다운 만료 후 재시도, 재시도/quota/timeout 파싱, 상태 갱신 |
| `VpnSystemTests` | 서버 카탈로그 캐시와 새로고침, connect 후 정책 사용 기록·현재 서버 노출 |
| `VpnRecoverySystemTests` | abort→connect→resume 순서, Network retry의 `RetryInfo` 전달, stall·attempt limit·connect failure 처리 |
| `AbortAndPromptRunnerTests` | abort 전용, prompt 전용, foreign busy 거부 |
| `MonitorSystemTests` | quota·rate limit·Network retry 이벤트의 `SessionErrorClassifiedEvent` 발행과 `Retry` 보존 |
| `RequestPipelineTests` | `vpn.policy` live query, `vpn.policy.set` command dispatch |
| `ErrorClassifierTests` | `free usage`, `subscribe to Go`, timeout 패턴 분류 |
| `app.integration.test.tsx` | VPN 정책 로드·저장, 정책 저장 command 접수와 mock 상태 반영 |

- 정책 파일은 `C:\AiCtrl\Agent\data\vpn-policy.json`이며 `temp` 파일 + `File.Move(overwrite: true)`로 원자적으로 교체한다. 파일이 없거나 손상되면 기본값 `enabled=true`, `minLoad=0`, `maxLoad=70`, `excludeUsedWithinHours=24`, `maxAttemptsPerSession=3`, `retryWaitThresholdSeconds=180`, `countries=[]`을 사용한다.
- 서버 상태는 `ok`, `quota_exhausted`, `opencode_timeout`, `connect_failed` 네 가지다. 후보는 허용 국가, 부하 범위, 최근 사용 제외 시간, 현재 서버 제외 조건을 만족해야 하며, 실패 상태는 `availableAt` 쿨다운이 지나기 전까지 제외한다. 쿨다운이 지나면 같은 서버를 다시 후보로 사용할 수 있다. 정렬은 부하 오름차순, 점수 내림차순, 동점 시 무작위다.
- 재시도 대기열은 `RetryInfo.NextAt`을 우선 사용하고, 없으면 메시지의 `재시도 중 N초 후`·`retry in N seconds`를 파싱한다. 대기 시간이 `retryWaitThresholdSeconds`보다 짧으면 같은 서버에서 대기 후 재개하고, 길거나 알 수 없으면 해당 서버를 쿨다운 처리하고 다른 서버를 선택한다.
- 복구 순서는 `AbortSessionAsync` → 정책 확인 → 필요 시 VPN 전환 → `PromptAsync`다. 전환 시 `force=true`로 연결하고 `status --json`의 터널 상태·서버 ID·출구 IP를 검증한다. 서버를 바꿨는데 출구 IP가 그대로면 연결 실패로 처리한다.
- 연결 실패 서버는 `connect_failed`와 24시간 쿨다운을 기록하고 다음 후보로 넘어간다. 한 번의 failover에서 후보 시도 횟수는 정책 값을 1~5로 clamp하며, 복구 시스템 전체의 세션당 복구 횟수는 기존 `vpn:recovery:maxAttemptsPerSession`(기본 3)이 별도로 제한한다.
- 프로토콜은 query `vpn.policy`, command `vpn.policy.set`이고 REST는 `GET`/`PUT /api/v1/machines/{machineId}/vpn/policy`다. UI는 정책을 30초마다 갱신하고, 저장 시 `PUT` + `wait=5`로 command를 보낸다. 202 pending이면 접수 토스트를 표시하고 정책·VPN page query를 무효화한다.
- 수동 서버 전환은 기존 `POST /vpn/connect`와 `force`를 그대로 사용한다. 별도 `/vpn/switch` endpoint는 추가하지 않았다.
- 한계: 실제 ProtonVPN connect/disconnect와 실제 OpenCode quota/timeout E2E는 검증하지 않았다. 분할 터널링, 프로토콜 선택, kill switch는 failover 정책 범위가 아니다. `maxAttemptsPerSession`은 현재 한 번의 전환에서 후보 서버 시도 횟수로 동작하며, UI 문구도 `전환당 후보 시도`로 맞췄다.
