# Web

## 1. 개요

`web/ai-ctrl-web`는 서버의 `/api/v1`을 사용하는 React SPA입니다. 별도 백엔드가 없고, 서버가 빌드 산출물을 정적 파일로 제공합니다.

| 항목 | 버전 |
| --- | --- |
| React | 19.3.1 |
| react-router-dom | 7.18.4 |
| TanStack Query | 5.104.5 |
| Zustand | 5.0.15 |
| Vite | 8.3.4 |
| TypeScript | 6.0.3 |
| Vitest | 5.0.5 |
| Playwright | 1.64.1 |
| MSW | 3.0.3 |
| oxlint | 1.88.0 |

Node.js 요구 사항은 README에 24 이상으로 적혀 있습니다. 이 PC의 Node 22.23.2에서 빌드·테스트가 동작하는 것은 확인했지만 공식 지원 범위는 아닙니다.

## 2. 스크립트

| 스크립트 | 동작 |
| --- | --- |
| `npm run dev` | Vite 개발 서버, `localhost:5173` |
| `npm run build` | `tsc -b` 후 `vite build` |
| `npm run preview` | 빌드 결과 미리보기 |
| `npm run typecheck` | `tsc -b` |
| `npm run lint` | `oxlint src` |
| `npm run test` | Vitest 1회 실행 |
| `npm run test:watch` | Vitest watch |
| `npm run test:e2e` | Playwright, MSW mock 모드 |
| `npm run test:e2e:real` | 실제 서버(`127.0.0.1:5080`) 대상 |

### 개발 서버 프록시

`vite.config.ts`는 `/api`만 `http://localhost:5080`으로 프록시합니다. `changeOrigin`은 false입니다.

- `/api` 외 경로는 Vite가 처리합니다.
- `/hubs`와 `/signin-google`은 프록시되지 않습니다.
- Google 로그인을 `localhost:5173`에서 시작하면 서버의 콜백은 `127.0.0.1:5080`에서 처리되고, 최종 화면은 서버 SPA로 이동합니다. 개발 중 인증은 `http://127.0.0.1:5080`에서 수행하는 편이 명확합니다.
- `VITE_API_BASE`를 지정하면 API 루트를 다른 origin으로 바꿀 수 있습니다. 이 경우 CORS와 쿠키 정책을 서버에 추가해야 합니다. 현재 서버에는 CORS가 없습니다.

## 3. 라우트

`AppRoutes.tsx` 기준입니다. 보호된 라우트는 `AuthGuard`와 `AppShell` 아래에 있습니다.

| 경로 | 화면 |
| --- | --- |
| `/login` | Google 로그인 안내 |
| `/` | 마지막 머신 또는 첫 머신으로 이동 |
| `/activity` | 활동 로그 |
| `/commands` | 명령 이력 |
| `/commands/:commandId` | 명령 상세와 단계 |
| `/settings` | 등록 코드, 머신 해제, 계정 정보 |
| `/m/:machineId` | AI Agents 대시보드 |
| `/m/:machineId/agents/:instanceId` | 인스턴스 세션 목록 |
| `/m/:machineId/agents/:instanceId/sessions/:sessionId` | 세션 대화와 프롬프트 |
| `/m/:machineId/files/*` | 워크스페이스 파일 탐색·미리보기·업로드·다운로드 |
| `/m/:machineId/vpn` | VPN 상태·국가·서버·정책 |
| `/m/:machineId/logs` | 서버 로그 목록 |
| `/m/:machineId/logs/:name` | 서버 로그 tail |
| `/m/:machineId/diagnostics` | 준비 중 placeholder |
| 그 외 | NotFound |

페이지 컴포넌트는 모두 lazy 로딩입니다. `Suspense` fallback은 "화면을 불러오는 중…" 블록입니다.

### 셸 구조

- 좌측 사이드바: 머신 선택, 콘솔 전역 링크(`/activity`, `/commands`, `/settings`), Agents·Workspace·Etc 섹션
- 상단 바: 선택 머신의 호스트명, 클라이언트 버전, 온라인 상태
- 모바일에서는 사이드바가 드로어로 열립니다.
- `lastMachineId`는 `localStorage`의 `ai-ctrl-ui`에 저장됩니다(`flux/uiStore.ts:21-24`).
- 사이드바 항목은 `features` 응답과 인스턴스 목록으로 구성됩니다(`app/featureRoutes.ts`). 관리되지 않는 인스턴스는 "읽기 전용" 배지가 붙습니다.
- Diagnostics는 `available=false`, "준비 중"으로 표시됩니다.

## 4. 인증 처리

- `useMe`가 `GET /api/v1/auth/me`를 호출합니다. 이 응답은 다른 API와 달리 봉투가 아닌 평문 객체입니다.
- `AuthGuard`는 최초 로드 시 401이면 `/login`으로 이동하고, 그 외 오류는 재시도 UI를 보여줍니다.
- `LoginPage`는 이미 로그인되어 있으면 원래 경로로 돌아갑니다. `returnUrl`은 `/`로 시작하는 경로만 허용합니다.
- 로그인 링크는 `/api/v1/auth/login?returnUrl=...`입니다.
- 쿠키는 HttpOnly이므로 프론트엔드가 토큰을 읽거나 저장하지 않습니다. `credentials: 'same-origin'`을 사용합니다.
- 로그아웃 API와 UI는 없습니다.
- 최초 인증 이후 쿼리에서 발생하는 401은 전역 리다이렉트로 처리하지 않습니다. 각 화면의 오류 블록에 표시됩니다. 쿠키가 만료된 상태에서 장시간 열어 둔 탭은 새로고침해야 로그인 화면으로 이동합니다.

## 5. API 클라이언트

`src/api/client.ts`가 단일 진입점입니다.

### 요청 규칙

- 기본 루트: `/api/v1` (`VITE_API_BASE`가 있으면 그 앞에 붙임)
- `Accept: application/json`
- JSON 본문이면 `Content-Type: application/json`
- Blob/ArrayBuffer면 `application/octet-stream` 또는 Blob의 type
- `X-Request-Id`, `Idempotency-Key`는 호출자가 전달
- `credentials: 'same-origin'`
- 쿼리 값이 `undefined`, `null`, `''`이면 생략

### 응답 처리

- `getEnvelope`, `postEnvelope`, `putEnvelope`은 `{data, meta}`를 검증합니다. `data`나 `meta`가 없으면 `invalid_response`로 실패합니다.
- `getPlain`은 봉투 없이 파싱합니다. `/auth/me`, enrollment code 생성에 사용합니다.
- `postCommand`는 응답의 `commandId` 또는 `requestId`를 보고 `detail`/`pending`을 판별합니다.
- 오류는 RFC 7807 ProblemDetails를 `ApiError`로 변환합니다. `X-Trace-Id`와 `X-Request-Id`를 보존합니다.
- `describeApiError`가 `machine_offline`, `agent_no_ack`, `query_timeout`, `prompt_unconfirmed`, `foreign_turn`, `reauth_required` 등에 한국어 안내를 매핑합니다.
- 성공 응답의 `X-Request-Id`는 `ApiResult.requestId`로 반환합니다.

### 명령 호출

- 웹은 모든 명령을 `wait=0`으로 호출하고 응답 후 1초 간격으로 상태를 polling합니다.
- prompt, abort, abort-and-prompt, permission reply, VPN connect/disconnect는 ULID를 `X-Request-Id`와 `Idempotency-Key`에 함께 사용합니다.
- VPN 정책 저장은 `PUT .../vpn/policy?wait=5`를 사용합니다.
- 파일 mkdir/delete/rename은 202를 받을 수 있으며, 1.5초 뒤 파일 목록을 다시 무효화합니다.
- 업로드는 생성 → chunk 순차 PUT → complete → 500ms 간격 상태 확인 순서입니다. 최대 20회(약 10초) 확인합니다.
- 다운로드는 생성 후 `ready`가 될 때까지 500ms 간격으로 최대 120회(60초) 확인하고 content URL로 이동합니다.
- 업로드 파일은 1개씩 순차 전송합니다. 병렬 업로드와 이어올리기는 지원하지 않습니다.

## 6. 데이터와 상태

### TanStack Query

전역 기본값은 `staleTime 5초`, 쿼리 재시도 1회, mutation 재시도 없음입니다(`app/App.tsx:9-30`). 400, 401, 403, 404, 409, 422, 429와 `machine_offline`은 재시도하지 않습니다.

| 데이터 | 주기 |
| --- | --- |
| machines | 10초 |
| machine 상세 | 10초 |
| instances | 15초 |
| features | 15초 |
| sessions | 5초 |
| session 상세 | 5초 |
| messages | 2초 |
| commands 목록 | pending 있으면 1초, 없으면 5초 |
| command 상세 | terminal 전까지 1초 |
| audit | 15초 |
| 서버 로그 목록 | 15초 |
| 서버 로그 tail | 5초 |
| workspaces | 20초 |
| files | 20초 |
| VPN page | 30초, busy면 2.5초 |
| VPN policy | 30초 |

모든 polling은 `refetchIntervalInBackground: false`입니다. 백그라운드 탭에서는 갱신되지 않습니다.

### Zustand

- `uiStore`: 사이드바 열림, 마지막 머신 ID. 마지막 머신만 persist합니다.
- `pendingPromptStore`: 전송 중·실패한 프롬프트를 세션 키별로 보관합니다. 낙관적 UI와 실패 재시도에 사용합니다.

### 실시간

`src/realtime/RealtimeAdapter.ts`에 인터페이스만 정의되어 있습니다.

- 이벤트 타입: `session.changed`, `command.changed`, `machine.changed`, `message.changed`
- 상태: `disconnected`, `connecting`, `connected`, `reconnecting`
- `connect`, `disconnect`, `subscribe`, `watchSession`, `unwatchSession`

구현체는 없고 어떤 컴포넌트도 연결하지 않습니다. UI Hub가 서버에 있지만 프론트엔드는 polling만 사용합니다.

## 7. 화면별 동작

### Agents와 Session

- 세션 목록은 `instance`와 `view`를 필수로 사용합니다. 기본은 `active`입니다.
- `includeChildren=true`로 자식 세션을 포함합니다.
- 관리 인스턴스에서는 `새 세션` 패널로 워킹 디렉토리, 제목, 첫 프롬프트, 모델을 지정해 세션을 만들 수 있습니다. 생성 직후 해당 세션 화면으로 이동합니다.
- 모델 목록은 하드코딩하지 않고 `GET /api/v1/machines/{machineId}/models`로 조회합니다. 서버는 `session.models` query를 에이전트에 전달하고, 에이전트는 OpenCode의 `/config/providers`(v2는 `/model`) 응답을 `ModelOption`으로 변환합니다. 응답이 없으면 `세션 기본 모델` 항목만 표시합니다.
- 워킹 디렉토리는 직접 입력하거나 `경로 선택` 대화상자로 고릅니다. 대화상자는 `빠른 위치`(바탕 화면, 문서, 다운로드, 사용자 폴더, AppData)와 드라이브를 트리 루트로 표시하고, 선택한 위치를 그 자리에서 펼칩니다. 차단된 경로는 `차단됨`으로 표시하고 펼칠 수 없습니다.
- 세션 생성 전에 `fs` 조회로 경로 존재 여부를 확인하고, 실패하면 명령을 보내지 않고 토스트로 안내합니다.
- `새 세션` 패널의 `오류 복구(failover)` 섹션은 머신 공통 `vpn.recovery` 중 사용 여부, 자동 VPN 전환, 자동 재개, 오류별 트리거, 재시작 프롬프트와 고급 설정(쿨다운·세션당 최대 시도·정체 임계값)을 편집합니다. 저장하면 이 머신의 모든 세션에 즉시 적용되며, 국가·도시·서버·Discord 같은 나머지 값은 VPN 페이지의 `VpnRecoveryPanel`에서 조정합니다.
- 세션 상태에 따라 prompt, abort, abort-and-prompt 버튼이 달라집니다.
- `Running`, `Retrying`, `Stalled`, `WaitingPermission`은 진행 중으로 표시합니다.
- `ForeignBusy`와 `Unknown`은 쓰기 작업을 비활성화합니다.
- 관리되지 않는 인스턴스는 읽기 전용입니다.
- 권한 응답 API는 있으나 세션 응답에 권한 요청 상세가 없어 UI 버튼을 제공하지 않습니다. "대기" 또는 "대기 중 오류"로만 표시합니다.
- 메시지는 2초마다 마지막 페이지를 다시 가져옵니다. `nextCursor`는 과거 메시지 추가 로드에 사용합니다. 서버 cursor가 oldest offset 기준이라 최신 50건 이후의 공백이 생길 수 있습니다(`API-GAPS.md`).
- Markdown은 `react-markdown` + `remark-gfm`으로 렌더링하고 원시 HTML은 렌더링하지 않습니다.
- 프롬프트는 optimistic bubble로 먼저 표시하고 실패 시 재시도 또는 제거할 수 있습니다.

### Files

- 워크스페이스 선택 후 경로 탐색, 정렬, 미리보기, 업로드, 다운로드를 제공합니다.
- 권한 플래그에 따라 버튼을 비활성화합니다. 서버도 명령 시점에 권한을 검사합니다.
- 목록은 500건 단위 무한 스크롤입니다.
- 미리보기는 텍스트/이미지만 지원합니다. 바이너리는 안내 후 다운로드로 유도합니다.
- 삭제는 확인 대화상자를 거치고 휴지통 이동임을 안내합니다.
- ZIP 다운로드는 디렉터리에만 허용됩니다. ZIP 내부 구조는 서버가 결정합니다.
- 업로드 SHA-256은 계산하지 않습니다.

### VPN

- `/vpn/page` 응답을 30초마다, 연결·해제 진행 중에는 2.5초마다 갱신합니다.
- 국가를 바꾸면 도시 선택을 초기화합니다.
- `force` 옵션으로 실행 중 세션을 무시할 수 있습니다.
- CLI 부재, helper 미응답, Proton 로그인 필요, GUI 실행 중, 세션 사용 중 상태를 구분해 버튼을 비활성화합니다.
- 자동 failover 정책은 30초마다 조회하고, 저장 시 `wait=5` 명령을 보냅니다.
- `VpnRecoveryPanel`은 복구 사용 여부, 오류별 트리거, 재시작 프롬프트, 국가·도시·허용 서버, 쿨다운, 세션당 최대 시도, 정체 임계값을 편집합니다. `vpn.recovery`를 30초마다 조회하고 저장 시 `wait=5` 명령을 보냅니다. 설정은 에이전트 재시작 없이 다음 오류 이벤트부터 적용됩니다.
- 복구 패널은 같은 화면에서 `vpn.recovery.set` 결과를 기다리며, pending이면 접수 안내를 표시합니다.
- 킬 스위치, 프로토콜, 포트 포워딩, 자동 재연결은 UI에서 제공하지 않습니다.

### Logs와 Commands

- 서버 로그만 조회합니다. 클라이언트 로그 API는 없습니다.
- 로그 파일이 없으면 "아직 로그 파일이 없습니다" 상태를 표시합니다.
- 명령 상세에는 machineId와 sessionId가 없어 관련 화면으로 바로 이동할 수 없습니다.
- `/commands` 필터는 machineId, status, limit입니다.

### Settings

- 등록 코드를 생성해 화면에 한 번 표시합니다. 코드는 메모리에만 보관합니다.
- 머신 해제는 확인 후 `DELETE /api/v1/machines/{machineId}`를 호출합니다.
- 계정 이메일과 인증 시각을 표시합니다.
- `VpnRecoveryNotificationPanel`에서 머신을 선택하고 Discord 알림 사용, 실패 전용 알림, webhook URL을 설정합니다. VPN 페이지의 복구 패널과 같은 `vpn.recovery` API를 사용합니다.
- Webhook 입력은 항상 비어 있고, 저장된 URL은 다시 표시하지 않습니다. `discordWebhookConfigured`가 true이면 "Webhook 설정됨" 배지를 표시하고 삭제 버튼을 제공합니다.
- webhook 삭제는 빈 문자열을 전송합니다. URL 필드를 비워 두고 저장하면 기존 webhook은 유지됩니다.
- 로그아웃 버튼은 없습니다.

## 8. 목(Mock)과 테스트

### MSW

- 개발 모드에서 `VITE_USE_MOCKS=true`일 때만 워커를 시작합니다(`main.tsx:7-10`).
- 핸들러는 `src/mocks/handlers.ts`, 데이터는 `fixtures.ts`에 있습니다.
- 목 상태는 새로고침 시 초기화됩니다.
- `vite.config.ts`의 `copyPublicDir: false` 때문에 `mockServiceWorker.js`는 `dist`에 포함되지 않습니다.
- Vitest는 `setupServer`를 사용합니다.

### Vitest

- 환경: jsdom, CSS 처리 활성화
- 설정 파일: `src/test/setup.ts`
- 테스트 파일:
  - `src/api/freshness.test.ts`
  - `src/app/featureRoutes.test.ts`
  - `src/features/agents/sessionOrder.test.ts`
  - `src/features/session/sessionActions.test.ts`
  - `src/test/app.integration.test.tsx`
- 통합 테스트는 MSW로 로그인, 머신, 세션 생성, 프롬프트, 파일, VPN 흐름을 검증합니다.

### Playwright

- mock 모드 기본: `npm run dev -- --host 127.0.0.1 --port 4173 --strictPort`를 자동 실행하고 `VITE_USE_MOCKS=true`를 주입합니다.
- 실제 모드: `PLAYWRIGHT_REAL=1`, `PLAYWRIGHT_BASE_URL` 설정, `playwright/.auth/user.json` storageState 사용
- 실제 모드 테스트는 `real-*.spec.ts`만 실행합니다.
- mock 테스트는 `files-vpn.spec.ts`입니다.
- workers 1, fullyParallel false입니다.
- 실패 시 trace, screenshot, video를 남깁니다.

## 9. 빌드와 배포

```powershell
cd web\ai-ctrl-web
npm install
npm run typecheck
npm run lint
npm run test
npm run build
```

- 결과: `web/ai-ctrl-web/dist`
- `index.html`은 `/src/assets/favicon.svg`를 참조하며 Vite가 hash된 asset으로 변환합니다.
- 서버는 `web.distPath`가 유효하면 `UseDefaultFiles` + `UseStaticFiles`로 제공합니다.
- SPA fallback은 `/api`, `/hubs`를 제외하고 `index.html`을 반환하며 `Cache-Control: no-cache`를 설정합니다.
- 정적 asset에는 별도 장기 캐시 헤더를 설정하지 않습니다.
- 서버와 다른 origin에 배포하려면 CORS와 쿠키 정책을 추가해야 합니다. 기본 구성은 same-origin 전제입니다.

## 10. 알려진 공백

`API-GAPS.md`와 코드를 기준으로 정리했습니다.

- 실시간 push 미사용. 최대 polling 간격만큼 지연됩니다.
- 세션 생성 API가 없습니다. 새 세션은 Agent/CLI에서 만들어야 합니다.
- 권한 요청 상세가 세션 응답에 없어 승인·거절 UI가 없습니다.
- Diagnostics API와 화면이 없습니다.
- 클라이언트 로그 조회가 없습니다.
- Diff API와 화면이 없습니다.
- 메시지 cursor가 과거 방향만 지원해 최신 구간과 과거 구간 사이에 공백이 생길 수 있습니다.
- 명령 상세에 machineId/sessionId가 없어 역추적 링크가 없습니다.
- 세션 목록은 첫 페이지만 조회합니다. 추가 로드 UI가 없습니다.
- Files: 병렬 업로드, 이어올리기, 업로드 SHA-256, 검색, 권한 변경이 없습니다.
- VPN: 터널 선택, 프로토콜, kill switch, 자동 재연결, 서버 ping이 없습니다.
- 로그아웃이 없습니다.
- 다크 테마만 제공합니다.
- 웹 빌드·테스트는 CI에 포함되지 않습니다.
