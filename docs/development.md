# 개발

## 1. 환경

| 항목 | 값 |
| --- | --- |
| .NET SDK | 9.0.100 이상, `rollForward: latestFeature` |
| 대상 프레임워크 | Core `net9.0`, Server/Client/Tests `net9.0-windows` |
| 테스트 러너 | Microsoft.Testing.Platform, xUnit v3 (`xunit.v3.mtp-v2` 4.0.2) |
| Node.js | README 기준 24 이상, npm 11 이상 |
| 패키지 버전 | `Directory.Packages.props`에서 중앙 관리 |
| 경고 | `TreatWarningsAsErrors=true` |
| 언어 | `LangVersion=latest`, Nullable, ImplicitUsings |

## 2. 빌드와 테스트

### 전체

```powershell
dotnet build AI-Ctrl-Agent.sln -c Release
dotnet test -c Release
```

`dotnet test`는 xUnit v3 MTP 러너를 사용합니다. 테스트 프로젝트는 `OutputType=Exe`와 `TestingPlatformDotnetTestSupport=true`를 사용합니다.

### 프로젝트 단위

```powershell
dotnet build src\AI-Ctrl-Agent.Core\AI-Ctrl-Agent.Core.csproj
dotnet build src\AI-Ctrl-Agent.Server\AI-Ctrl-Agent.Server.csproj -c Release
dotnet test tests\AI-Ctrl-Agent.Tests\AI-Ctrl-Agent.Tests.csproj -c Release
```

### 웹

```powershell
cd web\ai-ctrl-web
npm ci
npm run typecheck
npm run lint
npm run test
npm run build
```

- `npm ci`는 `package-lock.json`을 사용합니다.
- `npm run lint`는 oxlint입니다. ESLint가 아닙니다.
- E2E는 기본 mock 모드입니다. `npm run test:e2e`가 4173 포트에 개발 서버를 띄웁니다.
- 실제 서버 E2E는 `PLAYWRIGHT_REAL=1`, `PLAYWRIGHT_BASE_URL`, `playwright/.auth/user.json`이 필요합니다.
- Node가 PATH에 없다면 `C:\_Dev\node-22`를 PATH 앞에 추가합니다.

## 3. CI

`.github/workflows/ci.yml`은 `windows-latest`에서 다음만 수행합니다.

1. checkout
2. .NET 9 설정
3. `dotnet restore`
4. `dotnet build --no-restore -c Release`
5. `dotnet test -c Release`

웹 설치·타입 검사·린트·단위 테스트·빌드는 CI에 포함되지 않습니다. 웹 변경은 로컬에서 검증해야 합니다.

## 4. 저장소 구조

```text
src/
  AI-Ctrl-Agent.Core/        도메인, 프로토콜, 설정, 런타임, 로깅, 워크스페이스 정책
  AI-Ctrl-Agent.Server/      ASP.NET Core 서버, 엔드포인트, 허브, 저장소
  AI-Ctrl-Agent.Client/      Windows 에이전트, 시스템, OpenCode, VPN, 전송
  AI-Ctrl-Agent.ApiClient/   로컬 API 클라이언트
  AI-Ctrl-Agent.ClientMCP/   stdio MCP 서버와 도구 10개
  AI-Ctrl-Agent.ServerMCP/   빈 Generic Host (MCP 아님)
tests/
  AI-Ctrl-Agent.Tests/       xUnit v3 테스트
  AI-Ctrl-Agent.FakeOpenCode 가짜 OpenCode HTTP/SSE 서버
web/
  ai-ctrl-web/               React SPA
docs/
  ...                        이 문서들
```

의존 방향은 `Architecture/DependencyDirectionTests`가 검증합니다. Core는 Server/Client를 참조하지 않습니다. Server와 Client는 Core만 참조합니다.

## 5. 코드 규칙

### 네임스페이스와 프로젝트

- 어셈블리와 프로젝트 이름은 `AI-Ctrl-Agent.*`입니다.
- C# 네임스페이스와 루트는 `AiCtrl.*`입니다.
- 파일명은 타입명과 맞춥니다.
- record와 primary constructor를 적극적으로 사용합니다.
- 비동기 메서드는 `Async` 접미사와 `CancellationToken`을 사용합니다.
- 시간 의존 코드는 `TimeProvider`를 주입합니다.
- 경고를 남기지 않습니다. `TreatWarningsAsErrors` 때문에 빌드가 실패합니다.

### JSON

- 서버와 클라이언트는 `AcJsonContext`의 source-generated serializer를 사용합니다.
- 새 DTO를 추가하면 `[JsonSerializable(typeof(...))]`를 `AcJsonContext`에 등록합니다.
- 등록하지 않으면 런타임 직렬화에서 실패할 수 있습니다.
- API 응답 봉투는 `{ data, meta }`입니다. `meta`에는 `source`, `stale`, `asOf`가 포함됩니다.
- 오류는 RFC 7807 ProblemDetails와 `code`를 사용합니다.

### 런타임

- 에이전트 시스템은 `IAcSystem`을 구현하고 `DependsOn`으로 순서를 선언합니다.
- 새 시스템은 DI에 등록해야 위상 정렬에 포함됩니다.
- 장기 실행 루프는 `AcLoopSupervisor`로 감싸 예외 시 재시작되게 합니다.
- `InitAsync`는 3회 재시도, `StartAsync`는 재시도 없음, 종료는 역순 10초 타임아웃입니다.
- 시작 실패 시 롤백은 없습니다. 리소스 정리를 `StopAsync`에 의존하지 말고 `InitAsync` 실패 경로에서도 정리합니다.

### 설정

- 기본값은 `AcDefaultConfigs`에 둡니다.
- 옵션 클래스의 `FromConfiguration`에서 실제 키를 읽습니다.
- 기본 YAML에만 키를 추가하면 소비되지 않습니다.
- 키를 추가하면 `docs/config-reference.md`도 갱신합니다.
- 파싱 실패는 기본값으로 대체하는 패턴을 유지합니다.
- 설정은 핫 리로드되지 않는다고 가정합니다.

### 저장소

- EF Core `EnsureCreatedAsync`와 `ServerDb`의 raw `CREATE TABLE IF NOT EXISTS`를 함께 사용합니다.
- migration 파일은 없습니다. 스키마를 바꾸면 두 곳을 함께 수정합니다.
- 기존 DB에는 새 컬럼이 자동 추가되지 않습니다.
- SQLite 연결은 WAL을 사용합니다. 백업은 프로세스 중지 후 수행합니다.
- 트랜잭션 경계는 `ServerDb` 메서드 안에 둡니다.

### 보안

- 시크릿을 로그, 예외 메시지, 문서에 남기지 않습니다.
- 로그 redaction 대상은 authorization, bearer, `act_` 토큰, password, JWT, 등록 코드, `--code`입니다.
- 경로를 다룰 때는 `WorkspacePathPolicy`를 사용합니다. 직접 `Path.Combine` 후 파일 작업을 하지 않습니다.
- 새 엔드포인트에는 인증 정책을 명시합니다. 익명 허용은 등록·에이전트 인증·health로 제한합니다.
- 브라우저 쿠키는 `__Host-` 접두사와 `Secure`를 유지합니다.
- 에이전트 토큰의 machineId와 대상 리소스 소유자가 일치하는지 확인합니다.

### 웹

- API 호출은 `src/api/client.ts`를 통해서만 수행합니다.
- 서버 상태는 TanStack Query, UI 상태는 Zustand를 사용합니다.
- 컴포넌트 스타일은 CSS Modules를 사용합니다.
- Markdown은 `react-markdown`으로 렌더링하고 원시 HTML을 허용하지 않습니다.
- 새 쿼리는 `queryKeys`에 키를 추가하고 polling 정책을 명시합니다.
- mutation은 `ulid()`를 `X-Request-Id`와 `Idempotency-Key`에 함께 사용합니다.
- 실시간 이벤트를 추가할 때는 `RealtimeAdapter` 인터페이스와 polling 무효화를 함께 설계합니다.

## 6. 변경 절차

### Query 추가

1. `Core/Api` 또는 `Core/Protocol`에 타입과 payload를 추가합니다.
2. `AcJsonContext`에 등록합니다.
3. 서버 엔드포인트에서 `RemoteQueryService.QueryAsync`를 호출합니다.
4. 클라이언트 `ClientRequestHandler`에서 타입을 처리합니다.
5. `AgentCapabilities`에 지원 항목을 추가합니다.
6. 웹 타입·API·쿼리 훅을 추가합니다.
7. 서버·클라이언트·웹 테스트를 추가합니다.
8. `docs/server.md`, `docs/client.md`를 갱신합니다.

### Command 추가

1. `Core/Commands`에 payload와 타입 상수를 추가합니다.
2. `AcJsonContext`에 등록합니다.
3. 서버 엔드포인트와 `CommandExecutionService` 타임아웃 표를 확인합니다.
4. 클라이언트 `CommandSystem`에 핸들러를 추가합니다.
5. `ClientRequestHandler`의 지원 목록을 갱신합니다.
6. journal 멱등 처리와 terminal 결과 형식을 맞춥니다.
7. 웹 API·mutation·UI를 추가합니다.
8. `docs/server.md`, `docs/client.md`, `docs/web.md`를 갱신합니다.

### 엔드포인트 추가

1. `ServerEndpoints` 또는 `WorkspaceEndpoints`에 매핑합니다.
2. 인증 정책과 rate limit 필요 여부를 결정합니다.
3. 머신 소유권과 step-up 필요 여부를 확인합니다.
4. 응답 봉투와 오류 코드를 맞춥니다.
5. 웹 API 클라이언트와 테스트를 추가합니다.
6. `docs/server.md`의 엔드포인트 표를 갱신합니다.

### 설정 추가

1. `AcDefaultConfigs`에 기본값을 추가합니다.
2. 옵션 클래스와 `FromConfiguration`을 추가합니다.
3. 실제 사용처에 주입합니다.
4. 테스트를 추가합니다.
5. `docs/config-reference.md`에 소비 여부를 기록합니다.

## 7. 테스트 구성

### xUnit

- `Shouldly`로 단언합니다.
- `Microsoft.Extensions.TimeProvider.Testing`의 `FakeTimeProvider`를 사용합니다.
- 서버 통합 테스트는 `TestServerHost`로 실제 파이프라인을 구성합니다.
- OpenCode 의존 테스트는 `FakeOpenCodeServer`와 시나리오를 사용합니다.
- SignalR 테스트는 `Microsoft.AspNetCore.SignalR.Client`로 실제 연결을 만듭니다.
- 시간 기반 테스트는 실제 대기 대신 `FakeTimeProvider`를 사용합니다.
- 테스트 디렉터리: Client, Commands, Config, Domain, Logging, Providers, Runtime, Server, State, Storage, Systems, Transfers, Workspace, Architecture.

### 웹

- Vitest + jsdom + Testing Library + MSW입니다.
- `src/test/setup.ts`가 MSW 서버 수명주기를 관리합니다.
- 통합 테스트는 `src/test/app.integration.test.tsx`입니다.
- Playwright mock 테스트는 `e2e/files-vpn.spec.ts`, 실제 테스트는 `e2e/real-smoke.spec.ts`입니다.
- 실제 E2E는 로그인 storageState가 필요합니다.
- 테스트는 기본적으로 `restoreMocks`, `clearMocks`가 켜져 있습니다.

### 확인 명령

```powershell
dotnet test -c Release
cd web\ai-ctrl-web
npm run typecheck
npm run lint
npm run test
npm run build
```

## 8. 디버깅

### 로그

- 서버: 실행 디렉터리 `logs\yyyyMMdd-HHmmss-Server.txt`
- 클라이언트: 실행 디렉터리 `logs\yyyyMMdd-HHmmss-Client.txt`
- `AICTRL_LOG_DIR`로 위치 변경
- 기본 파일 레벨 Debug, 콘솔 Information
- ASP.NET Core scope의 TraceId·RequestId는 파일에 `{TraceId=..., RequestId=...}` 형태로 남습니다.

### 실행 중 확인

```powershell
Invoke-RestMethod http://127.0.0.1:5080/health
Get-NetTCPConnection -State Listen -LocalPort 5080,5081,4096,47800
```

OpenCode의 `/global/health`는 Basic 인증이 필요합니다. 인증 없이 호출하면 401이 정상입니다.

### 자주 쓰는 진단

- 웹 API 401: 쿠키는 HttpOnly이며 `credentials: 'same-origin'`이 필요합니다. `localhost`와 `127.0.0.1`을 섞지 않습니다.
- 웹이 비어 있음: `web.distPath`와 `index.html` 존재 여부를 확인합니다.
- 에이전트가 서버에 안 붙음: `client.db`의 `enrolled`와 재시작 여부를 확인합니다.
- OpenCode 시작 실패: `opencode.executable` 절대 경로와 4096 점유를 확인합니다.
- 파일 403: `allow*` 플래그, hard deny, reparse point를 확인합니다.
- 전송 실패: 서버 `data/transfers` 여유 공간과 transfer 상태를 확인합니다.

## 9. 알려진 결함과 제약

코드 분석 기준으로 확인된 항목입니다. 수정 시 문서도 함께 갱신해야 합니다.

| 항목 | 내용 |
| --- | --- |
| 설정 불일치 | `tunnel`, `forwardedHeaders`, `commands`, `queries`, `notifications`, `backup`, 클라이언트 `stream`/`transfers`/`power`/`errorRules`가 소비되지 않음 |
| 명령 만료 미검사 | 클라이언트가 `CommandEnvelope.ExpiresAt`를 실행 전에 확인하지 않음 |
| 대기 명령 미전달 | `HelloAck.PendingCommands`가 항상 빈 배열 |
| in-flight 누수 | `RemoteQueryService._inflight`의 SemaphoreSlim이 제거되지 않음 |
| 캐시 무한 증가 | `query_cache`에 만료·정리 정책이 없음 |
| 캐시 키 과다 | payload 전체가 키에 포함되어 cursor·limit마다 별도 캐시 생성 |
| 스트림 드롭 | ring byte 한도를 넘는 청크는 경고 후 버려짐 |
| 파일 deny 범위 | deny 패턴은 미리보기에만 적용. 목록·stat·mkdir·delete·rename에는 미적용. hard deny는 전체 적용 |
| 전송 재개 | 클라이언트 재개 루프는 수신만 처리. 송신 재개 없음 |
| OpenCode 모드 | `opencode.mode` 무시. 항상 채택/기동 |
| 모니터 설정 | `processScanSeconds`, `reconcileSeconds` 무시. 검사 주기 30초 고정 |
| 웹 실시간 | UI Hub 미연결. polling만 사용 |
| 웹 페이지네이션 | 세션 목록 첫 페이지만, 메시지 cursor는 최신 구간 공백 가능 |
| 권한 UI | 권한 요청 상세가 API에 없어 승인·거절 버튼 없음 |
| 진단·클라이언트 로그·Diff | API와 화면 없음 |
| 로그아웃 | 엔드포인트와 UI 없음 |
| ServerMCP | MCP 서버가 아닌 빈 호스트 |
| forwarded headers | 미들웨어 미배선 |
| CI | 웹 빌드·테스트 미포함 |
| GeoLite2 | 파일이 없으면 edge 국가 판별 실패 |
| 설정 검증 | `AcConfigValidator`는 런타임에 연결되지 않음 |
| DB migration | EF migration 없음. 스키마 변경 시 수동 처리 필요 |

## 10. 문서 규칙

- 문서는 UTF-8 with BOM, CRLF로 저장합니다.
- `.gitattributes`가 `*.md text eol=crlf`를 강제합니다.
- 코드 근거는 `파일:라인`으로 표기합니다.
- 확인하지 못한 내용은 "미확인"으로 표시합니다.
- 시크릿, 토큰, 등록 코드, 개인 이메일을 기록하지 않습니다.
- 문서 링크는 저장소 상대 경로 또는 inline code를 사용합니다.
- 새 엔드포인트·설정·명령을 추가하면 해당 문서와 `docs/README.md`의 색인을 함께 갱신합니다.
