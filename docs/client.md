# Agent (Client)

## 1. 실행 모드

`AI-Ctrl-Agent.Client`는 Windows 콘솔 애플리케이션입니다(`net9.0-windows`).

| 모드 | 명령 | 동작 |
| --- | --- | --- |
| 일반 | `AI-Ctrl-Agent.Client.exe` | 전체 시스템 시작 |
| 등록 | `AI-Ctrl-Agent.Client.exe enroll --code XXXX-XXXX [--server URL]` | 등록 후 종료 |
| VPN 헬퍼 | `AI-Ctrl-Agent.Client.exe --vpn-helper` | named pipe 서버만 실행 |

### 일반 시작 순서

1. `%APPDATA%\AiCtrl\client.yaml` 생성·로드 (`client.yaml` → `client.overrides.yaml` → `AICTRL__` 환경변수)
2. 로그 초기화
3. `%LOCALAPPDATA%\AiCtrl\client.db` 초기화 (WAL)
4. 중단된 journal 복구 (`RecoverInterruptedAsync`)
5. 옵션과 저장소 생성
6. DI 등록
7. `IAcSystem` 시작
8. `host.RunAsync()`로 콘솔 수명주기 유지

### 종료

- `AcApp.StopAsync`가 시작의 역순으로 시스템을 중지합니다. 각 중지에는 10초 타임아웃이 있습니다.
- `AcLoopSupervisor`가 등록한 백그라운드 루프를 취소합니다.
- 콘솔 Ctrl+C로 종료할 수 있습니다.

## 2. 시스템 구성

| 시스템 | `DependsOn` | 역할 |
| --- | --- | --- |
| `provider-host` | 없음 | OpenCode 백엔드 기동·감시, 인스턴스 발견 |
| `monitor` | `provider-host` | SSE 소비, 세션 상태·에러·권한 이벤트 발행 |
| `stream` | `monitor` | 세션 watch, 텍스트·툴·델타 청크 생성 |
| `state` | `monitor` | 세션 상태 투영, 스냅샷 |
| `vpn` | 없음 | ProtonVPN 상태·연결·서버 카탈로그·정책 |
| `vpn-recovery` | `vpn`, `monitor` | 오류·정지 시 재연결과 세션 재개 |
| `local-api` | `state` | `127.0.0.1:47800` 로컬 API |
| `server-link` | 없음 | SignalR 연결, 토큰, outbox |
| `transfers` | `server-link` | 업로드·다운로드 재개 |

위상 정렬 결과는 `provider-host → monitor → {stream, state} → vpn → vpn-recovery → local-api → server-link → transfers`입니다. 같은 레벨의 순서는 등록 순서를 따릅니다.

## 3. OpenCode 연동

### 백엔드 기동

`ProviderHostSystem`이 다음 순서로 동작합니다(`Systems/ProviderHostSystem.cs:100-163`).

1. `secrets.bin`에 `opencode.serve.password`가 없으면 32바이트 난수를 hex로 생성해 저장
2. `GET /global/health`로 기존 인스턴스 확인
3. 정상이면 프로세스를 소유하지 않고 채택(`Adopted`)
4. 비정상이면 `opencode serve --hostname <host> --port <port>` 실행
5. 환경변수 `OPENCODE_SERVER_USERNAME=opencode`, `OPENCODE_SERVER_PASSWORD=<secret>` 주입
6. `/config/providers` 등으로 인스턴스 발견, registry 교체
7. `provider-host:backend-watch` 루프로 상태 감시

`opencode.executable`이 PATH에 없으면 프로세스 시작이 실패합니다. 이때는 절대 경로를 지정해야 합니다.

```yaml
opencode:
  executable: 'C:\Users\<user>\AppData\Roaming\npm\node_modules\opencode-ai\bin\opencode.exe'
  host: 127.0.0.1
  port: 4096
```

`opencode.mode`와 `opencode.apiVersion`은 기본 YAML에 있지만 현재 코드에서 읽지 않습니다. 실제 동작은 항상 "헬스 프로브 후 채택 또는 기동"입니다.

### Supervisor 상태

`OpenCodeBackendSupervisor`는 `Stopped`, `Starting`, `Healthy`, `Unhealthy`, `Failed` 상태를 사용합니다.

- 헬스 폴링 500ms, 시작 타임아웃 20초, 감시 주기 10초
- 연속 실패 3회 시 재시작
- 재시작 백오프 5초 → 5분
- 채택한 인스턴스는 실패해도 재시작하지 않고 `Unhealthy`로 표시
- 직접 소유한 프로세스만 종료 대상
- 기존 포트가 Unauthorized/Foreign이면 `port_in_use`로 실패

### HTTP 경로

`OpenCodeRoutes`가 V1/V2 프리픽스를 처리합니다. 모든 요청은 Basic 인증을 사용하며 사용자명은 `opencode`, 비밀번호는 `opencode.serve.password`입니다.

| 기능 | 경로 |
| --- | --- |
| health | `/global/health` |
| OpenAPI | `/doc` |
| sessions | `/session` |
| status | `/session/status` |
| messages | `/session/{id}/message` |
| children | `/session/{id}/children` |
| prompt | `/session/{id}/prompt_async` |
| abort | `/session/{id}/abort` |
| permission | `/session/{id}/permissions/{permissionId}` |
| events | `/event` (SSE) |
| providers | `/config/providers` |

V1은 프리픽스 없음, V2는 `/api`를 사용합니다. `/doc`의 paths를 보고 자동 선택합니다.

### 세션과 메시지

- 세션 목록은 `/session`과 `/session/status`를 합쳐 매핑합니다.
- 메시지는 전체를 받아 클라이언트에서 커서 기반으로 자릅니다. 기본 50, 최대 200입니다.
- 프롬프트는 `parts:[{type:"text",text}]` 형식이며 선택적으로 `model`, `agent`, `messageID`를 보냅니다.
- 권한 응답은 `once`, `always`, `reject`입니다.
- SSE 재연결 프레임은 `SnapshotRequired` 이벤트로 변환합니다.

## 4. 명령 처리

### 지원 명령

| 타입 | 동작 |
| --- | --- |
| `session.abort` | 세션 중단. 자식 포함 옵션 |
| `session.prompt` | 프롬프트 전송 |
| `session.abort_and_prompt` | 중단 후 프롬프트 |
| `session.permission.reply` | 권한 응답 |
| `vpn.connect` | VPN 연결 |
| `vpn.disconnect` | VPN 해제 |
| `vpn.policy.set` | failover 정책 저장 |
| `files.receive` | 서버 → 클라이언트 파일 수신 |
| `files.send` | 클라이언트 → 서버 파일 송신 |
| `files.mkdir` | 디렉터리 생성 |
| `files.delete` | 휴지통으로 이동 |
| `files.rename` | 이름 변경 |
| `workspace.register` | 워크스페이스 등록 |
| `instance.refresh` | 성공만 반환하는 no-op |

### 미지원 명령

다음 4종은 `unsupported_command`로 실패합니다.

- `session.create`
- `machine.abort_all`
- `opencode.backend.restart`
- `power.keep_awake`

### 실행 절차

1. `CommandId` 기준 in-flight 중복 제거
2. journal에 멱등 기록 (`CreateIfAbsentAsync`)
3. 이미 terminal 결과가 있으면 재생
4. non-terminal 기록이 있으면 `duplicate_command`
5. `Ack` 콜백 호출 (실패해도 실행은 계속)
6. 타입별 작업 수행
7. `Progress`/`Respond` 전송, journal 갱신

세션 단위 직렬화는 `SessionActorPool`과 `SessionActor`가 담당합니다. `session.abort`는 우선 처리됩니다. `abort_and_prompt`는 abort 대기 15초(재시도 5초), 500ms 폴링, prompt 확인 10초를 사용합니다.

`CommandEnvelope.ExpiresAt`는 journal에 복사되지만 실행 전에 검사하지 않습니다.

## 5. 서버 링크

`ServerLinkSystem`은 다음 조건에서만 연결합니다(`Link/ServerLinkSystem.cs:111-136`).

- `serverUrl` 설정이 활성
- `client.db`의 `enrolled=true`
- 디바이스 키 존재

조건이 맞지 않으면 `Server link idle: client is not enrolled.` 또는 키 누락 경고를 남기고 종료합니다. 시작 시 한 번만 검사하므로 등록 후 재시작이 필요합니다.

### 연결 절차

1. `POST /api/v1/agent/challenge`
2. 디바이스 키로 `aictrl-agent-v1|{machineId}|{nonce}` 서명
3. `POST /api/v1/agent/token`
4. `/hubs/agent` WebSocket 연결
5. `Hello(machineId, version, bootId, lastAckedSeq, openCommandIds)`
6. outbox를 200건 단위로 `PushEvents`
7. 미보고 terminal 명령을 `Respond`로 재전송
8. `Request` 수신 시 `ClientRequestHandler` 실행
9. `SetWatchedSessions` 수신 시 `StreamSystem`에 전달

토큰은 만료 30초 전에 재발급합니다. 재연결 백오프는 지터 0.8~1.2배, 2배 증가, 상한 `reconnect.maxBackoffSeconds`(기본 30초)입니다. 네트워크 변경 시그널 또는 타이머 중 먼저 완료되는 쪽을 사용합니다.

### 디바이스 키

- ECDSA P-256
- PKCS#8 개인키를 DPAPI로 암호화해 `agent.device.key` 이름으로 `%LOCALAPPDATA%\AiCtrl\secrets.bin`에 저장
- 서명 대상: `aictrl-agent-v1|{machineId}|{nonce}`

## 6. 로컬 API

`LocalApiSystem`은 `127.0.0.1:47800`에만 바인딩합니다. 프리픽스는 `/local/v1`입니다.

### 인증

- Host가 `127.0.0.1` 또는 `localhost`이고 실제 포트와 일치해야 합니다.
- `Authorization: Bearer <local-token>` 고정시간 비교
- 토큰 파일: `%LOCALAPPDATA%\AiCtrl\local-token` (32바이트 난수의 hex)
- `localApi.port`를 0으로 지정하면 OS가 임의 포트를 배정합니다. 이 경우 `AICTRL_LOCAL_PORT`를 함께 맞춰야 합니다.

### 엔드포인트

| 메서드 | 경로 | 설명 |
| --- | --- | --- |
| GET | `/local/v1/status` | 에이전트 상태 |
| GET | `/local/v1/instances` | OpenCode 인스턴스 |
| GET | `/local/v1/instances/{instanceId}/sessions` | 인스턴스 세션 |
| GET | `/local/v1/sessions` | 세션 목록 |
| GET | `/local/v1/sessions/{sessionId}/messages` | 메시지. limit 1..200, 기본 20 |
| POST | `/local/v1/sessions/{sessionId}/abort` | 중단 |
| POST | `/local/v1/sessions/{sessionId}/prompt` | 프롬프트 |
| POST | `/local/v1/sessions/{sessionId}/abort-and-prompt` | 중단 후 프롬프트 |
| GET | `/local/v1/commands/{commandId}` | 명령 결과 |
| GET | `/local/v1/workspaces` | 워크스페이스 |
| GET | `/local/v1/files` | 파일 목록 |
| GET | `/local/v1/files/stat` | 파일 메타데이터 |
| GET | `/local/v1/files/preview` | 미리보기 |
| POST | `/local/v1/files/mkdir` | 디렉터리 생성 |
| POST | `/local/v1/files/delete` | 삭제 |
| POST | `/local/v1/files/rename` | 이름 변경 |

로컬 명령은 `Guid.NewGuid("N")`을 commandId로 사용하고, issuer `local`, TTL 5분을 적용합니다. HTTP 타임아웃은 60초이며 초과 시 504 `query_timeout`입니다.

### ClientMCP

`AI-Ctrl-Agent.ClientMCP`는 stdio MCP 서버입니다. `LocalApiClient`를 통해 로컬 API를 호출합니다.

- `local_status`
- `local_list_agent_instances`
- `local_list_sessions`
- `local_get_session_messages`
- `local_abort_session`
- `local_send_prompt`
- `local_abort_and_prompt`
- `local_list_workspaces`
- `local_list_files`
- `local_read_text_file`

변경 도구는 `confirm=true`가 없으면 미리보기만 반환합니다. 환경변수는 `AICTRL_LOCAL_PORT`(기본 47800), `AICTRL_LOCAL_TOKEN`(기본 토큰 파일 경로)입니다.

`LocalApiClient`에는 파일 생성·삭제·이름변경 메서드가 없습니다. 서버 엔드포인트와 비대칭입니다.

`ServerMCP`는 MCP 서버가 아닙니다. `AddAcRuntime`만 호출하는 빈 Generic Host입니다.

## 7. VPN

### 구성

- `VpnSystem`이 ProtonVPN CLI 또는 helper를 통해 상태를 조회하고 연결·해제합니다.
- `VpnRecoverySystem`은 오류·정지 이벤트에 반응해 재연결과 세션 재개를 수행합니다.
- `VpnPolicyStore`는 `%LOCALAPPDATA%\AiCtrl\vpn-policy.json`에 정책을 저장합니다.

### 연결 규칙

- CLI가 없으면 `CapabilityMissing`
- Proton 로그인 전이면 `VpnSigninRequired`
- Proton GUI가 실행 중이고 CLI가 앱 모드가 아니면 `ProtonGuiRunning`
- `force=false`이고 실행/재시도/권한대기/정지/외부 Busy 세션이 있으면 `SessionsBusy`
- 대상 서버는 명시 서버 → 국가/도시 순으로 선택합니다.
- fallback `TunnelReady`는 `http://127.0.0.1:20241/ready`를 최대 `minutes`까지 폴링합니다.
- fallback `ServerLink`는 서버 링크 연결을 같은 방식으로 기다립니다.
- fallback 실패 시 터널을 끊고 예외를 반환합니다.

### Helper

- named pipe: `aictrl-vpn-{SID}`
- 토큰: `%LOCALAPPDATA%\AiCtrl\vpn-helper.token`
- op: `ping`, `status`, `countries`, `servers`, `connect`, `disconnect`
- `--vpn-helper` 모드로 별도 프로세스를 실행합니다.

### 복구

기본 비활성입니다. 활성화하면 다음 이벤트를 구독합니다.

- `SessionErrorClassifiedEvent`
- `SessionStalledEvent`
- `SessionTurnCompletedEvent`

트리거는 QuotaExceeded, RateLimited(옵션), Network→Stalled, Stall 이벤트입니다. 처리 순서는 알림 → 세션 abort → VPN connect → 세션 재개입니다. 세션별 쿨다운 10분, 최대 3회이며 성공 턴 완료 시 카운터를 초기화합니다. Discord webhook은 HTTPS만 허용합니다.

## 8. 워크스페이스와 파일

### 설정

```yaml
workspaceParents:
  - 'C:\Work'
workspaces:
  - name: 'project'
    root: 'C:\Work\project'
    allowUpload: true
    allowDownload: true
    allowMkdir: true
    allowDelete: false
    denyPatterns: []
```

- `workspaces`와 `registeredWorkspaces`를 합칩니다.
- 루트 기준 중복은 제거됩니다.
- 기본 권한: upload/download/mkdir 허용, delete 거부
- `workspaceParents` 하위 경로만 신규 등록할 수 있습니다.
- 등록은 `client.overrides.yaml`의 `registeredWorkspaces:N:*`에 기록됩니다.
- 워크스페이스 ID는 정규화 경로의 SHA-256 앞 8자에 `ws_`를 붙입니다.

### 파일 정책

`WorkspacePathPolicy`는 다음을 수행합니다.

- 절대 경로, drive letter, `//`, `..`, `.`, trailing dot/space, 제어문자, DOS 예약명 거부
- root 하위 정규화
- reparse point를 최종 대상까지 해석한 뒤 다시 root 하위 검사
- 기본 deny 패턴 적용
- hard deny 경로 차단

기본 deny 패턴은 `.env`, `.env.*`, `*.pem`, `*.pfx`, `*.key`, `id_rsa*`, `.git/config`, `secrets.*`입니다. hard deny는 `%LOCALAPPDATA%\AiCtrl`와 `~/.local/share/opencode/auth.json`입니다.

적용 범위에 차이가 있습니다.

- 목록과 stat은 deny 여부를 플래그로만 표시합니다. stat은 deny된 파일의 해시도 계산할 수 있습니다.
- 미리보기는 deny 패턴이면 `path_denied`로 차단합니다.
- mkdir, delete, rename은 `IsDenied`를 호출하지 않고 권한 플래그만 확인합니다.
- hard deny 경로는 모든 작업에서 차단됩니다.

### 파일 작업

- 목록 최대 500건, 커서 기반
- stat hash는 512MiB 이하에서만 계산
- preview는 1MiB까지, 이미지는 5MiB까지 base64
- 텍스트는 UTF-8 실패 시 CP949로 fallback
- delete는 루트 삭제를 금지하고 휴지통으로 이동
- rename은 `allowDelete`를 재사용하며 루트 이름 변경을 금지

## 9. 전송

`TransferSystem`은 `%LOCALAPPDATA%\AiCtrl\transfers`를 임시 디렉터리로 사용합니다.

- 수신: 서버 transfer가 `direction=Upload`, `status=Ready`여야 합니다. workspace 일치와 `allowUpload`가 필요합니다. `.part`로 재개하고 크기·SHA-256을 검증합니다. 충돌 정책 `fail`, `overwrite`, `rename`을 적용합니다.
- 송신: `allowDownload`가 필요합니다. 디렉터리는 `asZip=true`일 때만 허용합니다. 서버가 준 chunk 크기로 PUT합니다.
- 재개 루프는 30초마다 실행되며 현재 수신만 재개합니다.
- `client.yaml`의 `transfers.*`는 클라이언트 코드에서 읽지 않습니다. chunk 크기는 서버 응답이 결정합니다.

## 10. 저장소와 로그

### 경로

| 대상 | 경로 |
| --- | --- |
| 설정 | `%APPDATA%\AiCtrl\client.yaml` |
| 오버라이드 | `%APPDATA%\AiCtrl\client.overrides.yaml` |
| DB | `%LOCALAPPDATA%\AiCtrl\client.db` |
| 로컬 API 토큰 | `%LOCALAPPDATA%\AiCtrl\local-token` |
| DPAPI 시크릿 | `%LOCALAPPDATA%\AiCtrl\secrets.bin` |
| VPN helper 토큰 | `%LOCALAPPDATA%\AiCtrl\vpn-helper.token` |
| VPN 정책 | `%LOCALAPPDATA%\AiCtrl\vpn-policy.json` |
| VPN 이력 | `%LOCALAPPDATA%\AiCtrl\vpn-history.json` |
| 전송 임시 | `%LOCALAPPDATA%\AiCtrl\transfers` |
| 로그 | 실행 파일 디렉터리 `\logs\yyyyMMdd-HHmmss-Client.txt` |

### 클라이언트 DB

| 테이블 | 내용 |
| --- | --- |
| `kv` | `machine_id`, `enrolled`, `server.url`, `outbox.next_seq`, `outbox.last_acked` |
| `journal` | 명령 상태와 결과, `reported` 플래그 |
| `outbox` | 서버로 보낼 이벤트 |
| `instance_session_seen` | 세션 발견 기록 |
| `transfers` | 진행 중 전송 |

### 로그

- 파일명: `yyyyMMdd-HHmmss-Client.txt`
- 파일 레벨 기본 `Debug`, 콘솔 기본 `Information`
- `AICTRL_LOG_DIR`로 디렉터리 변경 가능
- authorization, bearer, password, JWT, 등록 코드, `--code`는 redaction 대상입니다.

## 11. 설정 요약

전체 키와 소비 여부는 `config-reference.md`를 참조하십시오. 주요 키는 다음과 같습니다.

- `serverUrl`
- `localApi.port`, `localApi.tokenPath`
- `opencode.executable`, `opencode.host`, `opencode.port`, `opencode.instanceId`
- `monitor.statusPollSeconds`, `monitor.stallMinutes`, `monitor.logReasoning`, `monitor.logToolOutputMaxKB`
- `workspaceParents`, `workspaces`, `registeredWorkspaces`
- `vpn.*`
- `reconnect.maxBackoffSeconds`
- `logging.*`

다음 키는 기본 YAML에 있지만 클라이언트 코드에서 소비되지 않습니다.

- `opencode.mode`, `opencode.apiVersion`
- `monitor.processScanSeconds`, `monitor.reconcileSeconds`
- `stream.*`
- `transfers.*`
- `power.*`
- `errorRules`

## 12. 진단 체크리스트

| 증상 | 확인 |
| --- | --- |
| OpenCode 백엔드 시작 실패 | `opencode.executable` 경로, PATH, 4096 포트 점유 |
| 서버에 연결되지 않음 | `client.db`의 `enrolled`, 등록 후 재시작 여부, `serverUrl` |
| 로컬 API 401 | `local-token` 파일과 `Authorization: Bearer` 일치 여부 |
| 파일 전송 실패 | workspace 권한, 대상 부모 디렉터리, 충돌 정책 |
| VPN 연결 실패 | Proton CLI/로그인, helper 토큰, fallback ready URL |
| 세션 상태가 갱신되지 않음 | OpenCode health, SSE `/event`, `monitor` 로그 |
| 명령이 실행되지 않음 | journal의 중복 기록, 미지원 명령 타입, 세션 actor 대기 |
