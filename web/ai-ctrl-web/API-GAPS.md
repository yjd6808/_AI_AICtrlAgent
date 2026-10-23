# API 및 UI 제약

이 문서는 M5 프론트엔드가 M4 백엔드와 연동할 때 확인된 미구현 API, 계약상 제약, 프론트엔드가 의도적으로 제공하지 않는 기능을 정리합니다.

- 기준일: 2026-10-23
- 기준 계약: `src/AI-Ctrl-Agent.Core/Api/M4Contracts.cs`, `LocalApiContracts.cs`, `src/AI-Ctrl-Agent.Server/Endpoints/ServerEndpoints.cs`
- 원칙: 서버에 없는 기능을 가짜 데이터나 낙관적 UI로 대체하지 않습니다.

## 요약

| 영역 | M4 상태 | 현재 처리 | 영향 |
| --- | --- | --- | --- |
| 세션 생성 | 엔드포인트 없음 | 생성 UI 없음 | 새 세션은 기존 Agent/CLI에서 만들어야 함 |
| 권한 응답 | 응답 엔드포인트는 있으나 요청 상세 없음 | 승인/거부 UI 숨김 | `WaitingPermission`은 중단 또는 중단 후 전송만 가능 |
| 실시간 push | 브라우저용 채널 없음 | React Query polling | 최대 polling 간격만큼 지연 |
| Files | 조회/변경/전송 API 제공 | 실제 화면 | 진행률·재개 등 일부 제약 있음 |
| VPN | 상태/서버/연결·정책 API 제공 | 실제 화면 | 서버 목록 강제 갱신 UI 없음, 정책 저장은 command 접수 |
| Diagnostics | API 없음 | 준비 중 화면 | 진단 정보 수집 불가 |
| 클라이언트 로그 | 수집/조회 API 없음 | 서버 전역 로그만 표시 | 머신·세션별 Client 로그 확인 불가 |
| Diff | API 없음 | Diff 뷰어 없음 | 변경 파일 비교 불가 |
| 메시지 pagination | cursor 계약이 최신 N개 보장과 불일치 | `nextCursor`로 이전 메시지 추가 로드 | “최근 50개” 보장 불가 |
| 명령 상세 | `machineId`/`sessionId` 없음 | 명령 ID와 유형만 표시 | 머신/세션 역링크 구성 불가 |
| 세션 목록 pagination | `nextCursor`/`hasMore` 제공 | 첫 페이지와 5초 polling만 사용 | 세션 수가 많으면 추가 로드 필요 |
| 머신 텔레메트리 | health/workspace/VPN 필드가 비어 있음 | 파생 기능 패널 없음 | 상세 상태 시각화 제한 |

## 세부 내용

### 1. 세션 생성 API 부재

M4에는 세션 생성 엔드포인트가 없습니다. 다음 경로는 존재하지만 모두 기존 세션을 조회하거나 조작합니다.

- `GET /api/v1/machines/{machineId}/sessions`
- `POST /api/v1/machines/{machineId}/sessions/{sessionId}/prompt`
- `POST /api/v1/machines/{machineId}/sessions/{sessionId}/abort`
- `POST /api/v1/machines/{machineId}/sessions/{sessionId}/abort-and-prompt`

따라서 “새 세션” 버튼은 제공하지 않습니다. 세션은 Agent 또는 외부 CLI에서 생성한 뒤 목록 polling으로 확인합니다.

### 2. 권한 요청 상세 부재

권한 응답 엔드포인트 `POST /api/v1/machines/{machineId}/sessions/{sessionId}/permissions/{requestId}`는 존재합니다.

그러나 현재 `SessionSummary`/`SessionDetailView`에는 대기 중인 권한의 `requestId`, 도구 이름, 입력 요약, 선택 가능한 응답이 노출되지 않습니다. `displayState`가 `WaitingPermission`이라는 사실만으로는 안전하게 승인/거부 요청을 만들 수 없습니다.

현재 UI는 다음과 같이 동작합니다.

- 권한 승인/거부 버튼을 숨깁니다.
- “권한 승인 대기 중” 안내를 표시합니다.
- `중단`과 `중단 후 전송`만 제공합니다.

후속 API에서 권한 요청 상세가 노출되면 `usePermissionReply`를 사용해 응답 UI를 추가할 수 있습니다.

### 3. 실시간 채널 부재

M4 엔드포인트 목록에는 브라우저용 WebSocket/SSE/UI Hub가 없습니다. `src/realtime/RealtimeAdapter.ts`는 향후 연결을 위한 인터페이스만 정의합니다.

현재 갱신 주기는 다음과 같습니다.

- 머신 10초
- 인스턴스/기능 15초
- 세션 5초
- 메시지 2초
- 진행 중 명령 1초
- 종료된 명령 목록 5초
- 감사 5초 또는 15초
- 서버 로그 5초
- 파일 목록 20초
- VPN 상태 30초, 명령 처리 중 2.5초

파일 변경과 VPN 연결 명령 직후에는 즉시 무효화하고, 파일 변경 명령은 1.5초 뒤 한 번 더 무효화합니다. 실시간 이벤트가 추가되면 polling을 이벤트 기반 무효화로 대체해야 합니다.

### 4. Diagnostics API 부재

`/m/:machineId/diagnostics` 화면은 라우트와 준비 중 UI만 제공합니다. 가짜 진단 결과를 표시하지 않으며, 대응 API가 추가되면 placeholder를 실제 화면으로 교체해야 합니다. Files와 VPN은 전용 API가 제공되어 실제 화면을 사용합니다.

### 5. 클라이언트 로그와 Diff API 부재

M4가 제공하는 로그 API는 서버 전역 로그뿐입니다.

- `GET /api/v1/logs/server`
- `GET /api/v1/logs/server/{name}`

머신별 Client 로그, 세션별 실행 로그, 파일 Diff API는 없습니다.

현재 세션 화면은 메시지에 포함된 tool/error payload를 그대로 표시합니다. 별도 로그 뷰어나 Diff 뷰어는 제공하지 않습니다. 로그 화면 URL에 `machineId`가 포함되지만 조회 대상은 머신과 무관한 서버 로그입니다.

### 6. 메시지 cursor 의미

프론트엔드는 메시지 응답의 `nextCursor`를 `useInfiniteQuery.getNextPageParam`에 연결하고, “이전 메시지 더 불러오기”에 사용합니다.

M4 구현의 cursor는 oldest offset을 전진시키는 방식이므로 “최신 50개를 먼저 반환하고 더 오래된 메시지를 추가로 제공”하는 계약과 일치하지 않습니다. 장기 세션에서는 초기 화면에 기대한 최근 메시지가 포함되지 않을 수 있습니다.

권장 후속 조치:

- 서버에서 최신 페이지와 이전 페이지 방향을 명시적으로 구분
- 응답에 `hasOlder`/`hasNewer` 또는 양방향 cursor 추가
- 프론트엔드에서 초기 로드와 과거 로드의 정렬 방향을 분리

### 7. 명령 상세에 머신/세션 정보 없음

`CommandDetailView`에는 다음 필드만 있습니다.

- `commandId`
- `type`
- `status`
- `result`
- `errorCode`
- `errorMessage`
- `issuedBy`
- `issuedAt`
- `updatedAt`
- `steps`

`machineId`와 `sessionId`가 없으므로 `/commands/:commandId` 직접 진입 시 명령을 머신이나 세션 화면으로 연결할 수 없습니다. 명령 이력 목록에서 전달된 route state나 상위 문맥도 사용하지 않습니다. 필요하면 서버 계약에 대상 참조를 추가해야 합니다.

### 8. 세션 목록 pagination 미사용

`SessionPageDto`는 `nextCursor`와 `hasMore`를 제공하지만, 현재 Agents 화면은 `cursor=null`로 첫 페이지만 조회하고 추가 페이지를 요청하지 않습니다.

세션이 많은 인스턴스에서는 “더 보기” 또는 무한 스크롤을 추가해야 합니다. 세션 트리의 부모/자식 관계가 페이지 경계를 넘을 수 있으므로, 추가 구현 시 `includeChildren=true`와 루트/자식 중복 처리도 함께 검증해야 합니다.

### 9. 머신 텔레메트리 제약

`/machines` 응답의 일부 필드는 비어 있거나 `null`입니다.

- `instances`
- `features`
- `workspaces`
- `health`
- `vpn`

프론트엔드는 인스턴스, 기능, 워크스페이스, VPN을 전용 API로 조회합니다. `/machines` 응답의 해당 필드는 사용하지 않습니다. health 상세 패널은 데이터가 없으므로 표시하지 않으며, 텔레메트리 API가 추가되면 Settings/Diagnostics 화면에서 사용할 수 있습니다.

### 10. 서버 로그가 비어 있을 수 있음

개발 환경이나 로그 파일이 아직 생성되지 않은 환경에서는 `/api/v1/logs/server`가 빈 목록을 반환할 수 있습니다. 로그 화면은 이 경우 “로그 파일이 없습니다” 빈 상태를 표시하며, 오류로 처리하지 않습니다.

### 11. Files API 제약

Files 화면은 다음 API를 사용합니다.

- `GET /api/v1/machines/{machineId}/workspaces`
- `GET /api/v1/machines/{machineId}/workspaces/{workspaceId}/files`
- `GET /api/v1/machines/{machineId}/workspaces/{workspaceId}/files/preview`
- `POST /api/v1/machines/{machineId}/workspaces/{workspaceId}/files/mkdir`
- `POST /api/v1/machines/{machineId}/workspaces/{workspaceId}/files/delete`
- `POST /api/v1/machines/{machineId}/workspaces/{workspaceId}/files/rename`
- `POST /api/v1/machines/{machineId}/workspaces/{workspaceId}/transfers/uploads`
- `PUT /api/v1/machines/{machineId}/transfers/{transferId}/chunks/{index}`
- `POST /api/v1/machines/{machineId}/transfers/{transferId}/complete`
- `GET /api/v1/machines/{machineId}/transfers/{transferId}`
- `POST /api/v1/machines/{machineId}/workspaces/{workspaceId}/transfers/downloads`
- `GET /api/v1/machines/{machineId}/transfers/{transferId}/content`

제약과 현재 처리 방식은 다음과 같습니다.

- 목록은 cursor 기반이며 20초마다 polling합니다. 목록이 여러 페이지면 “더 불러오기”로 이어서 조회합니다. 실시간 갱신은 없습니다.
- `allowMkdir`, `allowDelete`, `allowUpload`, `allowDownload`는 서버가 제공하는 권한 표시입니다. UI는 이 값으로 버튼을 숨기지만 최종 검증은 서버가 수행합니다.
- 업로드는 파일 1개 단위입니다. 폴더 업로드, 다중 선택, 드래그 앤 드롭, 새로고침 후 재개를 지원하지 않습니다.
- 업로드 진행률은 브라우저가 전송한 chunk 기준입니다. 서버가 제공하는 전송 진행률 이벤트는 없습니다.
- `complete` 후 최대 약 10초 동안 전송 상태를 polling합니다. `completed`가 아니어도 `failed`/`expired`가 아니면 요청 성공으로 처리하므로, 실제 파일 반영은 목록 새로고침으로 확인해야 합니다.
- 업로드 파일의 `sha256`은 프론트엔드에서 계산하거나 검증하지 않습니다.
- 다운로드는 `ready`가 될 때까지 최대 약 60초 기다린 뒤 `/content` URL로 이동합니다. 다운로드 진행률은 표시하지 않으며, content는 서버 TTL 안에서만 유효합니다.
- 디렉터리 다운로드는 ZIP으로 요청합니다. ZIP 생성 진행률은 제공되지 않습니다.
- 텍스트 미리보기는 서버가 반환한 일부 텍스트만 표시하며(truncated 표시), 이미지 미리보기는 서버가 base64로 반환한 경우에만 동작합니다. 바이너리는 다운로드 버튼으로 대체합니다.
- `mkdir`, `delete`, `rename`은 `202 pending` 명령으로 접수될 수 있습니다. Files 화면은 명령 완료를 추적하지 않고 즉시 목록을 무효화한 뒤 1.5초 후 한 번 더 무효화합니다.
- 파일 생성·편집, 숨김 토글, 권한 변경, 파일 검색은 제공하지 않습니다.

### 12. VPN API 제약

VPN 화면은 다음 API를 사용합니다.

- `GET /api/v1/machines/{machineId}/vpn/page?country={code}&prevCity={city}`
- `GET /api/v1/machines/{machineId}/vpn/policy`
- `PUT /api/v1/machines/{machineId}/vpn/policy`
- `POST /api/v1/machines/{machineId}/vpn/connect`
- `POST /api/v1/machines/{machineId}/vpn/disconnect`
- `GET /api/v1/commands/{commandId}` (명령 완료 추적)

제약과 현재 처리 방식은 다음과 같습니다.

- 연결/해제는 비동기 명령입니다. 화면은 명령을 1초 간격으로 polling하고 최대 60초 후 결과 확인을 중단합니다. 중단 후에는 VPN 상태 새로고침으로 실제 연결 상태를 확인해야 합니다.
- VPN 상태는 30초, `busyOperation`/`busyCommandId`가 있으면 2.5초 간격으로 polling합니다.
- 서버 목록은 `/vpn/page` 응답의 캐시된 스냅샷을 사용합니다. 화면의 새로고침은 같은 스냅샷 API를 다시 호출할 뿐, 서버 목록 강제 갱신 API는 호출하지 않습니다.
- 국가를 바꾸면 도시와 서버 선택이 초기화됩니다. 도시를 선택하지 않으면 서버가 제안한 도시 또는 자동 선택을 사용합니다.
- `force`는 현재 연결을 강제로 교체할지 여부만 전달합니다.
- CLI 미탐지, Helper 미가용, ProtonVPN 미로그인, ProtonVPN GUI 실행 중 상태는 경고로 표시하며 연결 버튼을 제한합니다.
- 분할 터널링, 프로토콜 선택, kill switch, 자동 재연결, 서버 목록 새로고침 주기 설정은 제공하지 않습니다.
- VPN 정책은 Client의 `%LOCALAPPDATA%\AiCtrl\vpn-policy.json`에 저장되며, Server는 `vpn.policy` query와 `vpn.policy.set` command로만 접근합니다.
- `GET /vpn/policy`는 정책 설정과 서버별 상태(`ok`, `quota_exhausted`, `opencode_timeout`, `connect_failed`), `currentServer`, `updatedAt`을 반환합니다. 화면은 30초마다 갱신합니다.
- `PUT /vpn/policy`는 `wait=5`로 command를 보내며 202 pending을 반환할 수 있습니다. UI는 pending이면 접수 토스트를 표시하고 정책과 VPN page query를 무효화합니다.
- 정책의 `maxAttemptsPerSession`은 한 번의 failover에서 연결할 후보 서버 수 상한으로 동작하며 런타임에서 1~5로 제한됩니다. 복구 시스템 전체의 세션당 복구 횟수는 `vpn:recovery:maxAttemptsPerSession`이 별도로 제한합니다.
- 수동 서버 전환은 기존 `/vpn/connect`의 `server`/`country`/`city`와 `force`를 사용합니다. 별도 switch API는 없습니다.
