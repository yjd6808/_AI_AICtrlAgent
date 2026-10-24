# OpenCode Windows 서비스

`opencode serve`(127.0.0.1:4096)는 `AI-Ctrl-OpenCodeServer` Windows 서비스가 관리한다. 예약 작업이 아니라 SCM 서비스이므로 사용자가 로그온하지 않아도 부팅 시 시작된다.

## 구성

| 항목 | 값 |
| --- | --- |
| 서비스 이름 | `AI-Ctrl-OpenCodeServer` |
| 표시 이름 | AI-Ctrl OpenCode Server |
| 시작 유형 | 자동 (부팅 시) |
| 실행 계정 | 현재 Windows 사용자 (`.\<user>`) |
| 프로젝트 | `src/AI-Ctrl-Agent.OpenCodeService` (net48) |
| 서비스 호스트 | `C:\AiCtrl\OpenCode\bin\AiCtrlOpenCodeService.exe` |
| 설정 파일 | `C:\AiCtrl\OpenCode\bin\AiCtrlOpenCodeService.ini` |
| 엔드포인트 | `http://127.0.0.1:4096` |
| 로그 | `C:\AiCtrl\OpenCode\logs\service.log` |

서비스 호스트는 `opencode.exe serve --hostname 127.0.0.1 --port 4096`을 자식 프로세스로 실행한다. 자식이 종료되면 5초에서 시작해 최대 60초까지 백오프로 재시작하고, SCM 복구 정책도 5초/15초/60초 재시작으로 설정한다.

인증 정보는 `C:\AiCtrl\Agent\data\secrets.bin`의 `opencode.serve.password`를 DPAPI CurrentUser로 복호화해 `OPENCODE_SERVER_USERNAME`/`OPENCODE_SERVER_PASSWORD` 환경변수로 자식에 전달한다. 따라서 서비스 계정은 시크릿을 만든 사용자와 같아야 한다.

## 저장소 파일과 운영 스크립트

| 역할 | 경로 |
| --- | --- |
| 서비스 호스트 | `src/AI-Ctrl-Agent.OpenCodeService/` |
| 서비스 소스 | `src/AI-Ctrl-Agent.OpenCodeService/OpenCodeService.cs` |
| 빌드 | `C:\AiCtrl\Build-OpenCodeService.ps1`, `C:\AiCtrl\Build-Service.bat` |
| 설치 | `C:\AiCtrl\Install-OpenCodeService.ps1`, `C:\AiCtrl\Install-Service.bat` |
| 제거 | `C:\AiCtrl\Remove-OpenCodeService.ps1`, `C:\AiCtrl\Remove-Service.bat` |
| 예약 작업 폴백 | `C:\AiCtrl\Register-Service.ps1`, `C:\AiCtrl\Unregister-Service.ps1`, `C:\AiCtrl\OpenCodeServer.ps1` |

`C:\AiCtrl`은 배포 루트로, 실행 파일·설정·로그와 운영 스크립트가 함께 있다. 소스 코드는 저장소의 `src/AI-Ctrl-Agent.OpenCodeService`에만 둔다.

## 빌드와 배포

```powershell
dotnet build AI-Ctrl-Agent.sln -c Release
```

`Directory.Build.props`의 `AiCtrlDeploy` 타깃이 `bin\Release\net48`의 exe·pdb·config를 `C:\AiCtrl\OpenCode\bin`으로 복사한다. 서비스가 실행 중이면 exe가 잠겨 복사가 경고와 함께 건너뛰어지므로, 배포 전에 서비스를 중지한다.

```powershell
sc.exe stop AI-Ctrl-OpenCodeServer
dotnet build AI-Ctrl-Agent.sln -c Release
sc.exe start AI-Ctrl-OpenCodeServer
```

`-p:AiCtrlDeployOnBuild=false`로 자동 배포를 끌 수 있고, `-p:AiCtrlRoot=<경로>`로 배포 루트를 바꿀 수 있다.

서비스 호스트는 `--console`으로 콘솔 실행해 감시 로직을 점검할 수 있다.

```powershell
C:\AiCtrl\OpenCode\bin\AiCtrlOpenCodeService.exe --console --ini C:\AiCtrl\OpenCode\bin\AiCtrlOpenCodeService.ini
```

## 설치와 제거

```text
C:\AiCtrl\Install-Service.bat                 서비스 설치 (UAC + Windows 계정 비밀번호)
C:\AiCtrl\Remove-Service.bat                  서비스 제거
C:\AiCtrl\Remove-Service.bat -RestoreTask     서비스 제거 후 예약 작업으로 복구
```

`Install-OpenCodeService.ps1`의 기본 배포 루트는 `C:\AiCtrl`이며, `-AiCtrlRoot`로 바꿀 수 있다. 서비스 바이너리는 `C:\AiCtrl\OpenCode\bin`에서 찾는다.

`Install-OpenCodeService.ps1`의 동작 순서는 다음과 같다.

1. `opencode.serve.password` 복호화 확인
2. `SeServiceLogonRight` 부여 (`LsaAddAccountRights`)
3. 임시 서비스 `AI-Ctrl-OpenCodeServer-SelfTest`(포트 4098)로 실제 서비스 계정 기동 점검
4. 기존 예약 작업 중지·제거
5. 운영 서비스 생성, 계정 설정, 복구 정책 등록
6. 포트와 Basic 인증 API 확인

자체 점검에 실패하면 예약 작업을 건드리지 않고 중단한다. 운영 서비스 시작까지 실패하면 서비스를 제거하고 예약 작업으로 롤백한다. 서비스가 설치된 상태에서 `C:\AiCtrl\Register-Service.ps1`과 `C:\AiCtrl\Unregister-Service.ps1`은 충돌 방지를 위해 실행을 거부한다.

## 운영 명령

```powershell
Get-Service AI-Ctrl-OpenCodeServer
sc.exe query AI-Ctrl-OpenCodeServer
sc.exe qc AI-Ctrl-OpenCodeServer
sc.exe qfailure AI-Ctrl-OpenCodeServer

sc.exe stop AI-Ctrl-OpenCodeServer
sc.exe start AI-Ctrl-OpenCodeServer

Get-Content C:\AiCtrl\OpenCode\logs\service.log -Tail 30 -Wait
```

## 계정 비밀번호 변경

서비스는 Windows 계정 비밀번호를 SCM이 보관한다. 로그인 비밀번호를 바꾸면 서비스 시작이 1069로 실패한다.

```powershell
# 관리자 PowerShell
$cred = Get-Credential "$env:COMPUTERNAME\$env:USERNAME"
sc.exe config AI-Ctrl-OpenCodeServer obj= "$env:USERNAME" password= $cred.GetNetworkCredential().Password
Restart-Service AI-Ctrl-OpenCodeServer
```

마이크로소프트 계정으로 로그인하는 환경에서는 PIN이 아니라 계정 비밀번호를 사용해야 한다. SCM에 비밀번호를 저장하지 않으려면 `Install-OpenCodeService.ps1`을 실행하지 않고 예약 작업 방식(`C:\AiCtrl\Register-Service.bat`)을 사용한다.

## 진단

| 증상 | 확인 |
| --- | --- |
| 서비스가 시작되지 않음 (1069) | `Get-WinEvent -LogName System`에서 7038/7041, 계정 비밀번호와 `SeServiceLogonRight` |
| 서비스는 실행 중인데 4096이 닫힘 | `service.log`의 `opencode.serve.password is missing` 또는 `did not listen` |
| 401 응답 | `secrets.bin`의 `opencode.serve.password`와 `OPENCODE_SERVER_PASSWORD` 일치 여부 |
| exe 배포가 건너뛰어짐 | 서비스 실행 중 파일 잠금. 서비스 중지 후 빌드 |
| 자식 프로세스 반복 재시작 | `yyyyMMdd-HHmmss-opencode.err.log`, 포트 충돌 여부 |

서비스가 설치되어 있지 않으면 4096 포트는 예약 작업(`C:\AiCtrl\OpenCodeServer.ps1`)이 관리한다. 두 방식이 동시에 등록되면 포트 충돌이 발생하므로 하나만 사용한다.
