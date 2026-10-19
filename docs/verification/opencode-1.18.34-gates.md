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