# AI-Ctrl Agent Web (M5)

AI-Ctrl Agent의 원격 제어 콘솔입니다. React 19, TypeScript, Vite로 작성되었으며 M4 REST API와 연동합니다.

## 요구 사항

- Node.js 24 이상
- npm 11 이상
- M4 API 서버(`http://localhost:5080`) 또는 MSW 개발 모드

## 시작하기

```powershell
npm install
npm run dev
```

기본 개발 모드에서는 Vite가 `/api` 요청을 `http://localhost:5080`으로 프록시합니다. 별도 설정 없이 실제 M4 서버에 연결됩니다.

### MSW로 실행

백엔드 없이 화면과 명령 흐름을 확인하려면 `.env.local`에 다음 값을 추가합니다.

```dotenv
VITE_USE_MOCKS=true
```

`npm run dev`를 다시 시작하면 브라우저에서 `mockServiceWorker.js`가 등록되고 고정된 머신, 인스턴스, 세션, 메시지, 명령 데이터가 사용됩니다. 모의 데이터는 새로고침 시 초기화됩니다.

MSW는 개발 모드에서만 동작합니다. 프로덕션 빌드에는 mock 코드와 `mockServiceWorker.js`가 포함되지 않습니다.

## 스크립트

| 명령 | 설명 |
| --- | --- |
| `npm run dev` | Vite 개발 서버 실행 |
| `npm run build` | 타입 검사 후 프로덕션 빌드 생성 |
| `npm run preview` | 빌드 결과 미리보기 |
| `npm run typecheck` | TypeScript 프로젝트 빌드 검사 |
| `npm run lint` | oxlint 실행 |
| `npm run test` | Vitest 테스트 1회 실행 |
| `npm run test:watch` | Vitest 감시 모드 |

## 주요 경로

| 경로 | 화면 |
| --- | --- |
| `/login` | Google OIDC 로그인 안내 |
| `/` | 마지막 머신 또는 첫 머신으로 이동 |
| `/activity` | 감사 활동 로그 |
| `/commands` | 명령 이력 |
| `/commands/:commandId` | 명령 상세와 단계 |
| `/settings` | 계정, 머신 등록, 머신 해제 |
| `/m/:machineId` | AI Agents 대시보드 |
| `/m/:machineId/agents/:instanceId` | 선택한 인스턴스의 세션 목록 |
| `/m/:machineId/agents/:instanceId/sessions/:sessionId` | 세션 대화와 프롬프트 |
| `/m/:machineId/logs` | 서버 로그 |
| `/m/:machineId/files/*` | 워크스페이스 파일 탐색·미리보기·업로드·다운로드 |
| `/m/:machineId/vpn` | VPN 상태와 서버 선택·연결·해제 |
| `/m/:machineId/diagnostics` | Diagnostics 준비 중 안내 |

정의되지 않은 경로는 `NotFoundPage`로 처리됩니다.

## 구조

```text
src/
  api/          REST 클라이언트, 타입, React Query 훅, 포맷 유틸리티
  app/          앱 셸, 인증 가드, 라우팅, 기능 경로 계산
  components/   버튼, 배지, 토스트, 오류/로딩 상태 등 공용 UI
  features/     agents, session, files, vpn, activity, commands, logs, settings, placeholder
  flux/         Zustand 기반 UI 상태와 대기 중 프롬프트 상태
  lib/          공용 유틸리티
  mocks/        MSW 핸들러, fixture, 브라우저/테스트 서버
  realtime/     실시간 어댑터 인터페이스
  styles/       전역 디자인 토큰과 기본 스타일
  test/         Vitest 설정과 렌더 헬퍼
```

각 화면은 CSS Modules를 사용하며, 공용 색상과 간격은 `src/styles/global.css`의 CSS 변수로 관리합니다.

## API 연동 원칙

- 모든 요청은 same-origin `/api/v1` 경로와 `credentials: 'same-origin'`을 사용합니다.
- 인증은 HttpOnly 쿠키 기반이며 브라우저 저장소에 토큰을 저장하지 않습니다.
- Query 응답은 `{ data, meta }` 봉투를 사용합니다. `meta.source`, `meta.stale`, `meta.asOf`를 이용해 신선도를 표시합니다.
- 쓰기 명령은 하나의 ULID를 `X-Request-Id`와 `Idempotency-Key`에 함께 사용합니다.
- 명령은 `wait=0`으로 발행하고, terminal 상태가 될 때까지 1초 간격으로 최대 60초 동안 추적합니다.
- 자동 재시도는 조회에만 적용하고 쓰기 mutation에는 적용하지 않습니다.
- 401은 로그인 화면으로 이동하고, 403/404/409/503 등은 `ProblemDetails` 코드에 맞는 안내로 표시합니다.
- `machine_offline` 응답은 머신 상태를 다시 조회하고 쓰기 UI를 비활성화합니다.
- Markdown은 `react-markdown`과 `remark-gfm`으로 렌더링하며 원시 HTML을 허용하지 않습니다.

## 상태와 갱신

- 서버 상태는 TanStack Query가 관리합니다.
- 머신 10초, 인스턴스/기능 15초, 세션 5초, 메시지 2초, 감사 로그 15초, 서버 로그 5초 간격으로 갱신합니다.
- 파일 목록은 20초, VPN 상태는 30초(명령 처리 중 2.5초) 간격으로 갱신합니다.
- 진행 중인 명령은 1초, 종료된 명령 목록은 5초 간격으로 갱신합니다.
- UI 선택값, 마지막 머신, 세션 트리 확장 상태, 대기 중 프롬프트는 Zustand가 관리합니다.
- 실시간 push API가 없으므로 화면 갱신은 polling을 사용합니다.

## 세션 제어 규칙

- `Idle`, `Errored`, `Aborted`, `QuotaBlocked` 세션은 새 프롬프트를 바로 전송할 수 있습니다.
- `Running`, `Retrying`, `Stalled`, `WaitingPermission` 세션은 중단, 또는 중단 후 전송만 허용합니다.
- `ForeignBusy`와 `Unknown` 세션은 모든 쓰기 동작을 비활성화합니다.
- 관리형이 아닌 인스턴스는 읽기 전용입니다.
- 오프라인 머신에서는 캐시된 대화를 읽을 수 있지만 프롬프트를 전송할 수 없습니다.
- 자식 세션은 기본적으로 접히며, 부모 세션을 확장하면 표시됩니다.

## 파일과 VPN 규칙

- 파일 작업 버튼은 워크스페이스의 `allowMkdir`, `allowDelete`, `allowUpload`, `allowDownload` 값으로 표시합니다. 최종 권한 검사는 서버가 수행합니다.
- 업로드는 파일 1개 단위이며 충돌 정책(`fail`, `rename`, `overwrite`)을 선택할 수 있습니다. 업로드 완료 후 실제 파일 반영은 목록 새로고침으로 확인합니다.
- `mkdir`, `delete`, `rename`이 `202 pending`을 반환하면 명령 접수로 표시하고, 목록을 즉시 갱신한 뒤 1.5초 후 다시 갱신합니다.
- 다운로드는 서버가 `ready` 상태를 반환한 뒤 content URL로 이동합니다. content는 서버 TTL 안에서만 유효합니다.
- VPN 연결/해제는 비동기 명령이며 최대 60초 동안 결과를 추적합니다. 처리 중에는 연결 관련 버튼을 비활성화합니다.
- VPN 서버 목록은 `/vpn/page`의 캐시된 스냅샷을 사용하며, 국가를 바꾸면 도시와 서버 선택이 초기화됩니다.

## 보안

- 세션 쿠키 외의 인증 정보를 저장하지 않습니다.
- 로그아웃 API가 없으므로 로그아웃 버튼을 제공하지 않습니다.
- 등록 코드는 생성 응답을 받은 화면에서만 메모리에 표시하며 저장하지 않습니다.
- 머신 해제는 확인 대화상자를 거칩니다.
- 클립보드 복사는 사용자가 명시적으로 실행한 경우에만 수행합니다.
- UI의 버튼 비활성화는 편의를 위한 것이며 최종 권한 검사는 서버가 수행합니다.

## 테스트

```powershell
npm run test
```

테스트는 jsdom과 MSW `setupServer`를 사용합니다. 현재 다음을 검증합니다.

- 세션 상태 우선순위와 최신순 정렬
- 부모/자식 세션 트리 구성
- 세션 상태별 허용 동작
- 신선도와 포맷 유틸리티
- 401 로그인 리다이렉트
- 머신/인스턴스/읽기 전용 표시
- 오프라인 머신과 읽기 전용 세션의 입력 비활성화
- 권한 대기 세션의 중단 후 전송
- 프롬프트 202 응답부터 명령 성공까지의 polling 흐름
- 머신 목록 서버 오류 상태
- 명령 이력 렌더링
- 파일 목록 탐색과 텍스트 미리보기
- Files 도구 모음의 폴더 생성과 명령 접수
- 파일 업로드와 목록 반영
- VPN 연결 해제와 재연결 명령 추적

## 빌드와 배포

```powershell
npm run build
```

결과는 `dist/`에 생성됩니다. 프로덕션에서는 서버가 `dist`를 정적 파일로 제공하고, SPA 경로 fallback을 `index.html`로 연결해야 합니다. API는 같은 origin의 `/api/v1`에 있어야 하며, 다른 origin을 사용할 경우 쿠키와 CORS 정책을 별도로 구성해야 합니다.

세션 화면은 코드 분할되어 있으며, Markdown 렌더링 의존성은 세션 경로를 처음 열 때 로드됩니다.

## 알려진 API 제약

M4에 아직 없거나 프론트엔드에서 안전하게 사용할 수 없는 API는 `API-GAPS.md`에 정리되어 있습니다. 대표적으로 세션 생성, 권한 요청 상세, Diagnostics, 실시간 push, 클라이언트 로그, diff가 없습니다. Files와 VPN은 사용 가능하지만 업로드 재개, 서버 목록 강제 갱신 등 일부 제약이 있습니다.
