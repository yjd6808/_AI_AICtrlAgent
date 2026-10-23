# AI-Ctrl-Agent

Windows에서 OpenCode 기반 AI 코딩 세션을 원격에서 조회·제어하는 3계층 애플리케이션입니다. 서버가 브라우저와 에이전트를 중계하고, 에이전트가 로컬 OpenCode 인스턴스를 관리합니다.

| 구성 | 프로젝트 | 역할 |
| --- | --- | --- |
| Server | `src/AI-Ctrl-Agent.Server` | ASP.NET Core 9. REST API, SignalR 허브, SQLite, Google 로그인, 에이전트 인증 |
| Agent | `src/AI-Ctrl-Agent.Client` | Windows 콘솔 앱. OpenCode 백엔드, 로컬 API, VPN, 파일 전송, 서버 링크 |
| Web | `web/ai-ctrl-web` | React 19 + TypeScript + Vite SPA |
| 공용 | `src/AI-Ctrl-Agent.Core` | 도메인, 프로토콜, 설정, 런타임, 로깅 |
| 클라이언트 | `src/AI-Ctrl-Agent.ApiClient` | 로컬 API 클라이언트 |
| MCP | `src/AI-Ctrl-Agent.ClientMCP` | stdio MCP 서버. 로컬 API 도구 10개 제공 |
| MCP | `src/AI-Ctrl-Agent.ServerMCP` | 현재 빈 Generic Host. MCP 서버 아님 |
| 테스트 | `tests/AI-Ctrl-Agent.Tests`, `tests/AI-Ctrl-Agent.FakeOpenCode` | xUnit v3 + Shouldly, 가짜 OpenCode 서버 |

## 빠른 시작

### 사전 요구 사항

- Windows 10/11 x64
- .NET SDK 9.0.100 이상 (`global.json`의 `rollForward: latestFeature`)
- Node.js 24 이상, npm 11 이상 (웹 README 기준)
- OpenCode CLI. Managed 모드에서 `opencode serve`를 직접 실행합니다.
- Google OAuth Web 애플리케이션. 승인된 리디렉션 URI에 로그인에 사용할 주소를 등록해야 합니다.

이 PC에서 확인된 도구 경로는 다음과 같습니다.

- .NET SDK: `C:\Program Files\dotnet`
- Node.js: `C:\_Dev\node-22` (PATH에 등록되어 있지 않음)
- OpenCode: `C:\Users\<user>\AppData\Roaming\npm\node_modules\opencode-ai\bin\opencode.exe`

### 1. 빌드

```powershell
dotnet build AI-Ctrl-Agent.sln -c Release

cd web\ai-ctrl-web
npm install
npm run build
```

`dist`는 Git에 포함되지 않습니다. 서버가 SPA를 제공하려면 웹을 먼저 빌드해야 합니다.

### 2. 서버 설정과 시크릿

서버 설정 경로는 코드에 고정되어 있습니다.

- 설정: `C:\AiCtrl\Server\config\server.yaml`
- 오버라이드: `C:\AiCtrl\Server\config\server.overrides.yaml` (선택)
- 시크릿: `C:\AiCtrl\Server\config\secrets.bin` (Windows DPAPI, LocalMachine)
- DB: `C:\AiCtrl\Server\data\aictrl.db`
- 전송 파일: `C:\AiCtrl\Server\data\transfers`

`server.yaml`에 `auth.googleClientId`와 `auth.allowedEmails`를 설정합니다. Google 클라이언트 시크릿은 파일에 쓰지 않고 DPAPI 저장소에 넣습니다.

```powershell
& "src\AI-Ctrl-Agent.Server\bin\Debug\net9.0-windows\AI-Ctrl-Agent.Server.exe" secrets set google-client-secret
```

프롬프트에 값을 입력합니다. 표준 입력을 파이프로 넘겨도 됩니다. 시크릿을 변경한 뒤에는 서버를 재시작해야 합니다. 인증 스킴은 시작 시 한 번만 구성됩니다.

`googleClientId`와 `google-client-secret`이 모두 없으면 `/api/v1/auth/login`은 503을 반환하고 Google 스킴이 등록되지 않습니다.

### 3. 서버 실행

```powershell
dotnet run --project src\AI-Ctrl-Agent.Server
```

또는 빌드된 실행 파일을 직접 실행합니다.

```powershell
& "src\AI-Ctrl-Agent.Server\bin\Debug\net9.0-windows\AI-Ctrl-Agent.Server.exe"
```

`server.yaml`의 `kestrel.endpoints`가 기본 바인딩입니다.

- local: `http://127.0.0.1:5080` (REST, SPA, 로컬 개발)
- edge: `http://127.0.0.1:5081` (Cloudflare Tunnel 진입점)

`launchSettings.json`에는 5152가 적혀 있지만 실제 바인딩은 YAML의 Kestrel 설정을 따릅니다. `dotnet run` 사용 시 로그의 `Now listening on`으로 확인하십시오.

### 4. 에이전트 실행

```powershell
dotnet run --project src\AI-Ctrl-Agent.Client
```

에이전트는 시작 시 다음을 수행합니다.

1. `%APPDATA%\AiCtrl\client.yaml`이 없으면 기본값으로 생성
2. `%LOCALAPPDATA%\AiCtrl\client.db` 초기화 및 중단된 명령 복구
3. OpenCode 백엔드 기동 또는 기존 인스턴스 채택
4. 로컬 API `127.0.0.1:47800` 시작
5. 서버 링크 연결 시도

등록되지 않은 클라이언트는 서버 링크를 시작하지 않고 `Server link idle: client is not enrolled.`만 기록합니다.

### 5. 프론트엔드 실행

개발 서버:

```powershell
cd web\ai-ctrl-web
npm run dev
```

- 주소: `http://localhost:5173`
- `/api` 요청은 `http://localhost:5080`으로 프록시됩니다.
- `/hubs`와 `/signin-google`은 프록시되지 않습니다.

빌드 산출물을 서버에서 제공하는 방식:

```powershell
npm run build
```

`server.yaml`의 `web.distPath`가 `web\ai-ctrl-web\dist`를 가리키므로 서버가 `http://127.0.0.1:5080/`에서 SPA를 제공합니다. `/api`, `/hubs`를 제외한 경로는 `index.html`로 fallback됩니다.

### 6. 머신 등록

1. 서버가 제공하는 UI(`http://127.0.0.1:5080`)에서 Google 로그인
2. Settings 화면에서 등록 코드 생성 (8자, 유효 10분)
3. 에이전트에서 등록 실행

```powershell
& "src\AI-Ctrl-Agent.Client\bin\Debug\net9.0-windows\AI-Ctrl-Agent.Client.exe" enroll --code XXXX-XXXX --server http://127.0.0.1:5080
```

4. 에이전트 재시작

`ServerLinkSystem.StartAsync`는 등록 여부를 시작 시 한 번만 검사합니다. 등록 후 재시작하지 않으면 서버에 연결되지 않습니다.

## 로그인 경로 주의

Google OAuth 콜백 경로는 `/signin-google`입니다. Vite 개발 서버는 `/api`만 프록시하므로 `http://localhost:5173`에서 로그인을 시작하면 콜백이 서버에 도달하지 않습니다. 로그인과 등록은 서버가 직접 제공하는 `http://127.0.0.1:5080`에서 수행하십시오.

Google Cloud Console의 승인된 리디렉션 URI에는 실제로 사용하는 주소를 등록해야 합니다.

- `http://127.0.0.1:5080/signin-google`

운영 환경에서는 Cloudflare Tunnel 등 HTTPS 종단을 사용하고 그 주소의 콜백을 등록합니다.

## 실행 명령 요약

| 목적 | 명령 | 비고 |
| --- | --- | --- |
| 전체 빌드 | `dotnet build AI-Ctrl-Agent.sln -c Release` | 경고를 오류로 처리 |
| 전체 테스트 | `dotnet test -c Release` | Microsoft.Testing.Platform, xUnit v3 |
| 서버 실행 | `dotnet run --project src\AI-Ctrl-Agent.Server` | 설정은 `C:\AiCtrl\Server\config\server.yaml` |
| 에이전트 실행 | `dotnet run --project src\AI-Ctrl-Agent.Client` | 등록 전에는 서버 링크 유휴 |
| 에이전트 등록 | `AI-Ctrl-Agent.Client.exe enroll --code XXXX-XXXX` | 등록 후 에이전트 재시작 |
| VPN 헬퍼 | `AI-Ctrl-Agent.Client.exe --vpn-helper` | named pipe 서버 모드 |
| 서버 시크릿 | `AI-Ctrl-Agent.Server.exe secrets set <name>` | `google-client-secret` |
| 웹 개발 | `npm run dev` | `http://localhost:5173` |
| 웹 빌드 | `npm run build` | `dist` 생성 |
| 웹 타입 검사 | `npm run typecheck` | `tsc -b` |
| 웹 린트 | `npm run lint` | oxlint |
| 웹 단위 테스트 | `npm run test` | Vitest + MSW |
| 웹 E2E | `npm run test:e2e` | Playwright, 기본 mock 모드 |
| 정리 | `Clean.bat` | bin/obj/node_modules/dist 삭제 |

## 포트

| 포트 | 프로세스 | 용도 |
| --- | --- | --- |
| 5080 | Server | 로컬 REST, SPA, Google 콜백 |
| 5081 | Server | Cloudflare Tunnel edge 진입점 |
| 5173 | Vite | 개발 UI |
| 4173 | Vite | Playwright mock 모드 |
| 4096 | OpenCode | `opencode serve` HTTP API |
| 47800 | Agent | 로컬 API `/local/v1` |
| 20241 | 외부 | Cloudflare Tunnel metrics, VPN fallback ready URL |

## 주요 경로

| 대상 | 경로 |
| --- | --- |
| 서버 설정 | `C:\AiCtrl\Server\config\server.yaml` |
| 서버 시크릿 | `C:\AiCtrl\Server\config\secrets.bin` |
| 서버 DB | `C:\AiCtrl\Server\data\aictrl.db` |
| 서버 전송 | `C:\AiCtrl\Server\data\transfers` |
| 클라이언트 설정 | `%APPDATA%\AiCtrl\client.yaml` |
| 클라이언트 오버라이드 | `%APPDATA%\AiCtrl\client.overrides.yaml` |
| 클라이언트 DB | `%LOCALAPPDATA%\AiCtrl\client.db` |
| 로컬 API 토큰 | `%LOCALAPPDATA%\AiCtrl\local-token` |
| 클라이언트 시크릿 | `%LOCALAPPDATA%\AiCtrl\secrets.bin` (DPAPI, CurrentUser) |
| VPN 헬퍼 토큰 | `%LOCALAPPDATA%\AiCtrl\vpn-helper.token` |
| VPN 정책 | `%LOCALAPPDATA%\AiCtrl\vpn-policy.json` |
| 로그 | `<실행 파일 디렉터리>\logs\yyyyMMdd-HHmmss-{Server|Client}.txt` |

로그 디렉터리는 `AICTRL_LOG_DIR` 환경변수로 덮어쓸 수 있습니다. 기본 보존은 30일, 총 5GB입니다.

## 저장소 구조

```text
AICtrlAgent/
  AI-Ctrl-Agent.sln
  Directory.Build.props          net9.0, Nullable, TreatWarningsAsErrors
  Directory.Packages.props       중앙 패키지 버전
  global.json                    .NET 9.0.100, Microsoft.Testing.Platform
  Clean.bat
  src/
    AI-Ctrl-Agent.Core/          도메인, 프로토콜, 설정, 런타임, 로깅
    AI-Ctrl-Agent.Server/        ASP.NET Core 서버
    AI-Ctrl-Agent.Client/        Windows 에이전트
    AI-Ctrl-Agent.ClientMCP/     stdio MCP 서버
    AI-Ctrl-Agent.ServerMCP/     빈 Generic Host
    AI-Ctrl-Agent.ApiClient/     로컬 API 클라이언트
  tests/
    AI-Ctrl-Agent.Tests/         xUnit v3 테스트
    AI-Ctrl-Agent.FakeOpenCode/  테스트용 가짜 OpenCode 서버
  web/
    ai-ctrl-web/                 React SPA (Vite)
  docs/
    architecture.md
    server.md
    client.md
    web.md
    operations.md
    config-reference.md
    development.md
    verification/opencode-1.18.34-gates.md
```

## 런타임 구조

```text
브라우저 (Google 쿠키)
   │  REST /api/v1, SPA
   ▼
Server (5080/5081)
   │  SQLite, transfers
   │  SignalR /hubs/agent (agent token)
   ▼
Agent (Windows)
   │  HTTP 127.0.0.1:4096 (Basic auth)
   ▼
OpenCode serve
```

- 조회(Query): 브라우저 → 서버 → 에이전트 → OpenCode → 응답. 서버는 결과를 `query_cache`에 저장하고, 에이전트가 오프라인이면 stale 캐시를 반환합니다.
- 명령(Command): 브라우저 → 서버가 command 행 생성 → 에이전트 `Request` → `Ack` → `Progress` → `Respond`. 서버는 terminal이면 200, pending이면 202, 오프라인이면 503, 타임아웃이면 504를 반환합니다.
- 스트림: UI가 세션을 watch하면 서버가 lease를 관리하고, 에이전트가 `PushStream`으로 청크를 보냅니다. REST long-poll은 `epoch:seq` 커서를 사용합니다.
- 이벤트: 클라이언트 outbox에 영속되는 이벤트는 `vpn.state`뿐입니다. 세션 상태는 이벤트 소싱이 아니라 조회와 스트림으로 갱신됩니다.
- 전송: 업로드는 브라우저 → 서버 chunk → `files.receive` 명령, 다운로드는 `files.send` 명령 → 에이전트 → 서버 chunk → 브라우저입니다.

## 계약 요약

- REST prefix: `/api/v1`
- SignalR: `/hubs/agent` (WebSocket 전용, agent token), `/hubs/ui` (브라우저 정책)
- 프로토콜 버전: 1
- 요청 ID: 26자 Crockford Base32 ULID. `X-Request-Id`에 사용
- 멱등 키: `Idempotency-Key`, 최대 128자
- 등록 코드: `XXXX-XXXX` 형식, 10분 유효, SHA-256 해시로 저장
- 응답 봉투: `{ data, meta }`. `meta.source`, `meta.stale`, `meta.asOf` 포함
- 오류: RFC 7807 ProblemDetails + `code`

지원 Query는 15종, 클라이언트가 실제 처리하는 Command는 14종입니다. 전체 목록과 미지원 항목은 `docs/server.md`, `docs/client.md`를 참조하십시오.

## 보안 규칙

- Google 로그인 사용자는 `auth.allowedEmails`에 포함되어야 합니다.
- 세션 쿠키 이름은 `__Host-aictrl`이며 `Secure`, `HttpOnly`, `SameSite=Lax`입니다. HTTPS 또는 localhost 신뢰 컨텍스트가 필요합니다.
- 에이전트는 ECDSA P-256 디바이스 키로 challenge에 서명하고 단기 토큰을 발급받습니다.
- 서버 시크릿은 DPAPI LocalMachine, 클라이언트 시크릿은 DPAPI CurrentUser로 보호됩니다.
- 로컬 API는 `127.0.0.1`/`localhost` Host와 Bearer 토큰을 검증합니다.
- 워크스페이스 경로는 root 하위로 정규화하고 reparse point를 최종 대상까지 해석합니다. 기본 deny 패턴과 hard deny 경로가 있습니다.
- GeoGate는 local 포트를 우회하고 edge 포트에서 `CF-Connecting-IP`를 요구합니다. 국가를 확인할 수 없으면 `failClosed=true`일 때 403입니다.
- 등록·challenge·token 엔드포인트는 IP당 1분 5회로 제한됩니다.
- 시크릿, 쿠키, 토큰, 등록 코드를 문서나 로그에 기록하지 마십시오.

## 개발 규칙

- 빌드: `dotnet build AI-Ctrl-Agent.sln -c Release`
- 테스트: `dotnet test -c Release`
- 웹 검증: `npm run typecheck`, `npm run lint`, `npm run test`
- `TreatWarningsAsErrors=true`입니다. 경고를 남기지 마십시오.
- `bin`, `obj`, `dist`, `node_modules`는 직접 수정하지 않습니다.
- JSON은 `AcJsonContext`의 source-generated serializer를 사용합니다. 새 DTO를 추가하면 컨텍스트 등록도 필요합니다.
- 설정 키를 추가할 때는 반드시 옵션 클래스의 `FromConfiguration`에서 읽도록 배선합니다. 기본 YAML에만 키를 추가하면 소비되지 않습니다.
- 문서는 UTF-8 with BOM, CRLF로 저장합니다. 저장소의 `.gitattributes`도 `*.md text eol=crlf`입니다.
- 커밋은 사용자가 명시적으로 요청할 때만 수행합니다.

## 변경 시 체크리스트

- Command/Query 타입 추가: `Core/Commands` 또는 `Core/Protocol` → 서버 엔드포인트 → `ClientRequestHandler`/`CommandSystem` → 웹 타입·쿼리 훅
- 설정 추가: `AcDefaultConfigs` → 옵션 클래스 → 사용처 → `docs/config-reference.md`
- 엔드포인트 추가: `ServerEndpoints`/`WorkspaceEndpoints` → 인증 정책 → 웹 API 클라이언트 → 테스트
- DB 변경: `AcDbContext`와 `ServerDb`의 raw DDL을 함께 수정합니다. EF migration은 사용하지 않고 `EnsureCreated`와 `CREATE TABLE IF NOT EXISTS`를 사용합니다.
- 시크릿 변경: 서버 재시작 필요
- 등록 변경: 에이전트 재시작 필요

## 알려진 함정

1. `opencode`가 PATH에 없으면 에이전트가 백엔드를 시작하지 못합니다. `opencode.executable`에 절대 경로를 지정하십시오.
2. 등록 전에는 `ServerLinkSystem`이 유휴 상태로 남습니다. 등록 후 에이전트를 재시작해야 합니다.
3. Google 시크릿 변경은 서버 재시작 전까지 반영되지 않습니다.
4. YAML 설정은 핫 리로드되지 않습니다. 변경 후 프로세스를 재시작하십시오.
5. Vite 개발 서버는 `/api`만 프록시합니다. Google 콜백 `/signin-google`은 서버 주소에서 처리해야 합니다.
6. `web.distPath`가 존재하지만 `index.html`이 없으면 UI를 제공하지 않습니다. `npm run build`로 dist를 생성하십시오.
7. GeoLite2 데이터베이스가 없으면 edge 포트에서 국가 판별이 실패합니다. `failClosed=true`이면 차단됩니다.
8. `ServerMCP`는 MCP 서버가 아닙니다. 실제 MCP 도구는 `ClientMCP`에 있습니다.
9. 기본 YAML의 일부 키는 코드에서 소비되지 않습니다. `docs/config-reference.md`의 미소비 목록을 확인하십시오.
10. 서버 `query_cache`는 자동 정리되지 않습니다. 페이로드가 캐시 키에 포함되어 cursor·limit 조회마다 행이 늘어납니다.

## 문서

| 문서 | 내용 |
| --- | --- |
| `docs/architecture.md` | 전체 구조, 요청·데이터 흐름, 생명주기, 설계 한계 |
| `docs/server.md` | REST 엔드포인트, 허브, 인증, 저장소, 스트림, 전송 |
| `docs/client.md` | 에이전트 시스템, OpenCode 연동, 명령, 로컬 API, VPN, 워크스페이스 |
| `docs/web.md` | 라우트, API 클라이언트, 상태 관리, 목, 테스트, 빌드 |
| `docs/operations.md` | 초기 설정, 등록, 재시작, 배포, 진단 |
| `docs/config-reference.md` | 서버·클라이언트 설정 키와 실제 소비 여부 |
| `docs/development.md` | 빌드·테스트·CI, 코드 규칙, 알려진 결함 |

## 현재 PC 설정 메모

이 저장소를 이 PC에서 실행할 때 확인된 값입니다. 다른 환경에서는 경로가 달라질 수 있습니다.

- `%APPDATA%\AiCtrl\client.yaml`의 `opencode.executable`을 `C:\Users\<user>\AppData\Roaming\npm\node_modules\opencode-ai\bin\opencode.exe`로 지정해야 백엔드가 시작됩니다.
- Node.js가 PATH에 없으므로 웹 명령은 `C:\_Dev\node-22`를 PATH 앞에 추가하거나 전체 경로로 실행합니다.
- `server.yaml`의 `web.distPath`는 `<repo>\web\ai-ctrl-web\dist`입니다.
- Google 로그인 리디렉션 URI는 `http://127.0.0.1:5080/signin-google` 기준으로 등록되어 있어야 합니다.
