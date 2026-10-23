# 운영과 배포

## 1. 사전 준비

| 항목 | 요구 사항 |
| --- | --- |
| OS | Windows 10/11 x64 |
| .NET | SDK 9.0.100 이상. `global.json`은 `latestFeature`로 롤포워드 |
| Node.js | README 기준 24 이상, npm 11 이상 |
| OpenCode | `opencode serve` 실행 가능한 CLI |
| Google | OAuth 2.0 Web 애플리케이션 |
| 네트워크 | 외부 배포 시 HTTPS 종단과 Cloudflare Tunnel 권장 |

이 저장소를 이 PC에서 운영할 때 확인된 경로는 다음과 같습니다.

- .NET: `C:\Program Files\dotnet`
- Node.js: `C:\_Dev\node-22` (PATH 미등록)
- OpenCode: `C:\Users\<user>\AppData\Roaming\npm\node_modules\opencode-ai\bin\opencode.exe`

## 2. 최초 설치

### 2.1 빌드

```powershell
dotnet build AI-Ctrl-Agent.sln -c Release

cd web\ai-ctrl-web
npm install
npm run build
```

`dist`는 저장소에 포함되지 않으므로 웹을 먼저 빌드해야 합니다.

### 2.2 서버 설정 생성

서버를 한 번 실행하면 `C:\AiCtrl\Server\config\server.yaml`이 기본값으로 생성됩니다. 실행 후 바로 종료해도 됩니다.

```powershell
dotnet run --project src\AI-Ctrl-Agent.Server
```

이후 `server.yaml`에서 최소한 다음 값을 수정합니다.

```yaml
auth:
  googleClientId: '<Google OAuth client ID>'
  allowedEmails:
    - 'user@example.com'
web:
  distPath: 'C:\Users\<user>\Desktop\Programming\Dev\AICtrlAgent\web\ai-ctrl-web\dist'
```

설정 파일 경로는 코드에 고정되어 있습니다. 다른 디렉터리로 옮길 수 없습니다.

- `C:\AiCtrl\Server\config\server.yaml`
- `C:\AiCtrl\Server\config\server.overrides.yaml` (선택)
- `C:\AiCtrl\Server\config\secrets.bin`
- `C:\AiCtrl\Server\data\aictrl.db`
- `C:\AiCtrl\Server\data\transfers`

### 2.3 Google 시크릿 저장

시크릿은 YAML에 쓰지 않습니다. DPAPI 저장소에 넣습니다.

```powershell
& "src\AI-Ctrl-Agent.Server\bin\Release\net9.0-windows\AI-Ctrl-Agent.Server.exe" secrets set google-client-secret
```

프롬프트에 값을 입력합니다. 표준 입력을 파이프로 넘길 수도 있습니다.

```powershell
'GOCSPX-...' | & "AI-Ctrl-Agent.Server.exe" secrets set google-client-secret
```

시크릿은 `LocalMachine` DPAPI로 암호화되므로 같은 머신에서만 복호화됩니다. 다른 머신으로 복사해도 사용할 수 없습니다.

### 2.4 Google OAuth 설정

Google Cloud Console에서 OAuth 동의 화면과 Web 애플리케이션 클라이언트를 만듭니다.

- 승인된 JavaScript 원본: 사용하지 않음
- 승인된 리디렉션 URI:
  - 로컬: `http://127.0.0.1:5080/signin-google`
  - 운영: `https://<도메인>/signin-google`
- 테스트 사용자 또는 게시 상태를 실제 로그인 사용자에 맞게 설정

`localhost`와 `127.0.0.1`은 서로 다른 origin입니다. 실제 접속 주소와 정확히 일치하는 URI를 등록해야 합니다.

### 2.5 서버 시작

```powershell
dotnet run --project src\AI-Ctrl-Agent.Server
```

또는 빌드 결과를 직접 실행합니다.

```powershell
& "src\AI-Ctrl-Agent.Server\bin\Release\net9.0-windows\AI-Ctrl-Agent.Server.exe"
```

확인:

```powershell
Invoke-RestMethod http://127.0.0.1:5080/health
curl.exe -s -i http://127.0.0.1:5080/api/v1/auth/login
```

`/health`는 200과 버전을 반환해야 합니다. 로그인 요청은 Google로 302 리다이렉트되어야 합니다. 503이면 `googleClientId` 또는 시크릿이 없습니다.

### 2.6 에이전트 설정

`%APPDATA%\AiCtrl\client.yaml`을 확인합니다. OpenCode가 PATH에 없다면 절대 경로를 지정합니다.

```yaml
serverUrl: 'http://127.0.0.1:5080'
localApi:
  port: 47800
opencode:
  executable: 'C:\Users\<user>\AppData\Roaming\npm\node_modules\opencode-ai\bin\opencode.exe'
  host: 127.0.0.1
  port: 4096
```

### 2.7 등록

1. 서버가 제공하는 UI(`http://127.0.0.1:5080`)에서 Google 로그인
2. Settings에서 등록 코드 생성
3. 에이전트 등록

```powershell
& "src\AI-Ctrl-Agent.Client\bin\Release\net9.0-windows\AI-Ctrl-Agent.Client.exe" enroll --code XXXX-XXXX --server http://127.0.0.1:5080
```

4. 에이전트 재시작

```powershell
dotnet run --project src\AI-Ctrl-Agent.Client
```

등록 코드는 10분간 유효하고 1회만 사용할 수 있습니다. 등록 후 재시작하지 않으면 서버 링크가 시작되지 않습니다.

### 2.8 확인

- 서버 UI에 머신이 온라인으로 표시됩니다.
- 에이전트 로그에 OpenCode 백엔드가 `Healthy`로 기록됩니다.
- 로컬 API가 응답합니다.

```powershell
$token = Get-Content "$env:LOCALAPPDATA\AiCtrl\local-token" -Raw
Invoke-RestMethod -Headers @{ Authorization = "Bearer $token" } http://127.0.0.1:47800/local/v1/status
```

## 3. 실행 순서

1. OpenCode가 필요하면 먼저 실행하거나 에이전트가 기동하도록 둡니다.
2. 서버를 시작합니다.
3. 에이전트를 시작합니다.
4. 브라우저에서 서버 UI에 접속합니다.

에이전트가 OpenCode를 관리하므로 OpenCode를 미리 띄울 필요는 없습니다. 다만 이미 실행 중인 인스턴스가 있으면 채택합니다.

## 4. 운영 배포

### 4.1 게시

```powershell
dotnet publish src\AI-Ctrl-Agent.Server -c Release -r win-x64 --self-contained false -o publish\server
dotnet publish src\AI-Ctrl-Agent.Client -c Release -r win-x64 --self-contained false -o publish\client
```

- 서버는 `publish\server`에서 실행합니다. 로그는 실행 디렉터리의 `logs`에 생성됩니다.
- 클라이언트는 `publish\client`에서 실행합니다.
- `server.yaml`과 `secrets.bin`은 게시 디렉터리에 포함되지 않습니다. `C:\AiCtrl\Server\config`를 별도로 유지합니다.
- `web.distPath`는 게시 디렉터리의 절대 경로로 다시 지정합니다.

### 4.2 서비스 등록

두 실행 파일 모두 콘솔 앱이며 Windows 서비스로 직접 등록할 수 없습니다. NSSM, WinSW, 작업 스케줄러 중 하나로 감싸서 실행합니다.

- 서버는 시스템 부팅 후 자동 시작, 실패 시 재시작 정책을 설정합니다.
- 클라이언트는 사용자 세션에서 실행해야 합니다. DPAPI `CurrentUser`, ProtonVPN, OpenCode가 사용자 컨텍스트에 의존합니다.
- 클라이언트를 서비스 세션에서 실행하면 디바이스 키와 VPN helper 토큰을 복호화하지 못합니다.

### 4.3 외부 노출

권장 구성은 Cloudflare Tunnel → `http://127.0.0.1:5081`입니다.

- 5080은 로컬 전용으로 유지합니다.
- 5081은 `CF-Connecting-IP`가 없으면 403입니다.
- Cloudflare Tunnel은 이 헤더를 자동으로 추가합니다.
- `geo.allowCountries`와 `geo.allowCidrs`를 실제 운영 환경에 맞게 설정합니다.
- `geo.dbPath`에 GeoLite2-Country 데이터베이스를 배치합니다. 파일이 없으면 국가 판별이 실패합니다.
- `geo.failClosed`가 true이면 판별 실패 시 차단됩니다.
- Google Console의 리디렉션 URI에 운영 HTTPS 주소를 등록합니다.
- 세션 쿠키가 `__Host-` 접두사를 사용하므로 HTTPS가 필수입니다.

방화벽에서 5080과 5081을 외부에 직접 열지 않습니다.

### 4.4 프록시 헤더

서버는 `UseForwardedHeaders`를 배선하지 않았습니다. 프록시 뒤에서 원 클라이언트 IP가 필요하면 `CF-Connecting-IP`를 사용하는 GeoGate만 동작합니다. 다른 리버스 프록시를 사용한다면 forwarded headers 미들웨어 추가가 필요합니다.

## 5. 설정 변경

- `server.yaml`과 `client.yaml`은 시작 시 한 번만 읽습니다. 변경 후 프로세스를 재시작합니다.
- `server.overrides.yaml`과 `client.overrides.yaml`도 같은 시점에 병합됩니다.
- `AICTRL__` 환경변수로 특정 키를 덮어쓸 수 있습니다. 예: `AICTRL__auth__allowedEmails__0`.
- YAML 시퀀스는 환경변수에서 인덱스로 표현해야 하므로 길어집니다. 빈번한 변경은 오버라이드 파일을 사용합니다.
- 시크릿 변경은 `secrets set` 후 서버 재시작이 필요합니다.
- 등록 상태 변경은 에이전트 재시작이 필요합니다.

## 6. 재시작이 필요한 변경

| 변경 | 서버 재시작 | 클라이언트 재시작 |
| --- | --- | --- |
| `server.yaml` | 필요 | - |
| `google-client-secret` | 필요 | - |
| 웹 `dist` 재빌드 | 불필요 | - |
| `client.yaml` | - | 필요 |
| 등록(`enroll`) | - | 필요 |
| 워크스페이스 등록 | - | 불필요 (`workspace.register`로 반영) |
| VPN 정책 | - | 불필요 (파일 저장) |
| 로그 레벨 | 필요 | 필요 |

## 7. 백업과 복구

### 백업 대상

| 대상 | 경로 | 비고 |
| --- | --- | --- |
| 서버 설정 | `C:\AiCtrl\Server\config\server.yaml` | 시크릿 제외 |
| 서버 시크릿 | `C:\AiCtrl\Server\config\secrets.bin` | 같은 머신에서만 복호화 |
| 서버 DB | `C:\AiCtrl\Server\data\aictrl.db` | 명령·감사·캐시 |
| 전송 파일 | `C:\AiCtrl\Server\data\transfers` | 진행 중 전송, TTL 24시간 |
| 클라이언트 설정 | `%APPDATA%\AiCtrl\client.yaml` | 시크릿 제외 |
| 클라이언트 DB | `%LOCALAPPDATA%\AiCtrl\client.db` | 등록 상태, journal, outbox |
| 클라이언트 시크릿 | `%LOCALAPPDATA%\AiCtrl\secrets.bin` | 사용자 DPAPI |
| VPN 정책 | `%LOCALAPPDATA%\AiCtrl\vpn-policy.json` | |

### 절차

1. 서버와 에이전트를 중지합니다.
2. `server.yaml`, `aictrl.db`, 선택적으로 `transfers`를 복사합니다.
3. 서버를 재시작합니다.
4. 에이전트를 재시작합니다.

SQLite는 WAL 모드를 사용하므로 실행 중 복사는 일관성을 보장하지 않습니다. 반드시 중지 후 복사하거나 SQLite backup API를 사용합니다.

`secrets.bin`은 머신/사용자에 바인딩됩니다. 다른 머신으로 이전하면 Google 시크릿을 다시 저장하고 에이전트를 다시 등록해야 합니다.

### 머신 해제와 재등록

- Settings에서 머신을 삭제하면 서버의 machines 행과 관련 상태가 제거됩니다.
- 기존 에이전트는 토큰 발급에 실패합니다.
- 같은 에이전트를 다시 쓰려면 새 등록 코드로 `enroll`을 다시 실행하고 재시작합니다.
- `client.db`를 삭제하면 등록 상태, journal, outbox가 사라집니다. 재등록이 필요합니다.

## 8. 업그레이드

1. 서버와 에이전트를 중지합니다.
2. DB와 설정을 백업합니다.
3. 새 바이너리와 웹 `dist`를 배치합니다.
4. 서버를 시작해 스키마 생성을 확인합니다.
5. 에이전트를 시작합니다.
6. UI에서 머신·세션·파일 접근을 확인합니다.

EF migration이 없으므로 테이블 구조가 바뀐 버전은 기존 DB에 새 컬럼을 자동 추가하지 않을 수 있습니다. 업그레이드 노트에 스키마 변경이 있으면 DB를 재생성하거나 수동 마이그레이션해야 합니다.

## 9. 진단

### 포트와 프로세스

```powershell
Get-NetTCPConnection -State Listen -LocalPort 5080,5081,5173,4173,4096,47800 |
  Select-Object LocalAddress, LocalPort, OwningProcess
Get-Process AI-Ctrl-Agent.Server, AI-Ctrl-Agent.Client, opencode -ErrorAction SilentlyContinue
```

### 서버

```powershell
Invoke-RestMethod http://127.0.0.1:5080/health
Get-ChildItem "C:\AiCtrl\Server\config"
Get-ChildItem "C:\AiCtrl\Server\data"
Get-ChildItem "C:\AiCtrl\Server\data\transfers"
```

`/api/v1/logs/server`는 로그인 쿠키가 필요합니다. 쿠키 없이 확인하려면 로그 파일을 직접 읽습니다.

```powershell
Get-ChildItem .\logs\*-Server.txt | Sort-Object LastWriteTime -Descending | Select-Object -First 3
Get-Content (Get-ChildItem .\logs\*-Server.txt | Sort-Object LastWriteTime -Descending | Select-Object -First 1) -Tail 100
```

### 에이전트

```powershell
Get-ChildItem "$env:APPDATA\AiCtrl"
Get-ChildItem "$env:LOCALAPPDATA\AiCtrl"
Get-Content "$env:APPDATA\AiCtrl\client.yaml"
Get-Content .\logs\*-Client.txt -Tail 100
```

로그에서 다음 문자열을 확인합니다.

- `Server link idle: client is not enrolled.` → 등록 후 재시작 필요
- `port_in_use` → OpenCode 포트 충돌
- `opencode` 실행 실패 → `opencode.executable` 경로 확인
- `capability_missing` → VPN CLI/helper 없음
- `foreign_turn` → 다른 앱이 세션을 점유
- `unsupported_command` → 미지원 명령

### OpenCode

```powershell
curl.exe -s -u opencode:<password> http://127.0.0.1:4096/global/health
curl.exe -s -u opencode:<password> http://127.0.0.1:4096/config/providers
```

OpenCode는 Basic 인증을 요구합니다. 인증 없이 호출하면 401입니다. 비밀번호는 `%LOCALAPPDATA%\AiCtrl\secrets.bin`의 `opencode.serve.password`에 있지만, 값을 출력하거나 셸 기록에 남기지 않습니다.

### 웹

```powershell
Test-Path .\web\ai-ctrl-web\dist\index.html
Invoke-WebRequest http://127.0.0.1:5080/ -UseBasicParsing | Select-Object StatusCode, Content
```

dist가 없으면 `/`는 `AI-Ctrl-Agent Server` 문자열을 반환합니다. 이는 오류가 아니라 SPA 미배포 상태입니다.

## 10. 장애 대응

| 증상 | 원인 후보 | 확인 |
| --- | --- | --- |
| 로그인 503 | clientId/시크릿 누락 | `/auth/login` 응답, 서버 로그 |
| `redirect_uri_mismatch` | Console URI 불일치 | 접속 주소와 `/signin-google` 조합 |
| `email_not_allowed` | 허용 목록 누락 | `auth.allowedEmails` |
| 로그인 후 401 반복 | 쿠키 미설정 | HTTPS 여부, `__Host-` 쿠키, 브라우저 쿠키 저장 |
| 머신 없음 | 미등록 | 에이전트 로그, Settings 코드 |
| 머신 오프라인 | 에이전트 미실행/미등록 | 47800, 4096, 클라이언트 로그 |
| OpenCode 시작 실패 | PATH/포트/권한 | `opencode.executable`, 4096 점유 |
| 202가 계속됨 | 명령이 pending | `/commands/{id}` 상태, 클라이언트 로그 |
| 503 `machine_offline` | 링크 끊김 | 서버 로그, 네트워크 |
| 504 `query_timeout` | OpenCode 응답 지연 | OpenCode health, 세션 크기 |
| 파일 403 | 권한 플래그/deny | 워크스페이스 설정 |
| 업로드 실패 | 디스크/크기/충돌 | transfer 상태, 서버 data 여유 공간 |
| VPN 연결 불가 | CLI/로그인/세션 사용 중 | VPN 페이지 상태, helper 로그 |
| edge 403 | CF 헤더/국가/DB | 5081 직접 접속, `geo.dbPath` |
| 웹 빈 화면 | dist 누락/경로 오류 | `web.distPath`, index.html |
| 설정이 반영되지 않음 | 핫 리로드 없음 | 프로세스 재시작 |
| 시크릿 변경 무효 | 재시작 안 함 | 서버 재시작 |
| `__Host` 쿠키 거부 | 비HTTPS 비루프백 | 접속 주소 변경 |

## 11. 보안 운영

- 시크릿과 등록 코드를 문서, 채팅, 로그에 남기지 않습니다.
- `secrets.bin`과 `client.db`는 ACL로 보호합니다. 서버 데이터 디렉터리는 서버 실행 계정만 접근하도록 설정합니다.
- `auth.allowedEmails`를 최소 인원으로 유지합니다.
- 외부 노출 시 GeoGate와 Cloudflare 접근 정책을 함께 사용합니다.
- 5080을 외부에 열지 않습니다.
- Google 클라이언트 시크릿을 교체하면 `secrets set` 후 재시작합니다.
- 등록 코드는 사용 후 즉시 만료됩니다. 사용하지 않은 코드도 10분 뒤 만료됩니다.
- 로그 redaction은 authorization, bearer, password, JWT, 등록 코드, `--code`를 대상으로 합니다. 그 외 민감 값을 로그에 남기지 않도록 코드를 수정해야 합니다.
- `transfers`에는 업로드·다운로드 파일이 최대 24시간 남습니다. 운영 정책에 맞게 TTL과 디스크를 관리합니다.
- 감사 로그(`/api/v1/audit`)를 주기적으로 확인합니다.

## 12. 현재 PC 확인 상태

2026-10-23 기준 확인 결과입니다.

- 서버: `127.0.0.1:5080`, `127.0.0.1:5081` listen
- 에이전트: 로컬 API `47800`, OpenCode `4096` 정상
- 웹: Vite `5173`에서 200
- Google 로그인: `/api/v1/auth/login` → 302 `accounts.google.com`, redirect_uri `http://127.0.0.1:5080/signin-google`
- 등록: 서버 DB에 머신·사용자·코드가 없음. Google 로그인 후 코드 발급과 `enroll`이 남아 있음
- `%APPDATA%\AiCtrl\client.yaml`의 `opencode.executable`은 절대 경로로 수정됨
- Node.js는 PATH에 없어 `C:\_Dev\node-22`를 사용해야 함
- GeoLite2 DB는 아직 배치되지 않음. 로컬 5080 운영에는 영향 없음
