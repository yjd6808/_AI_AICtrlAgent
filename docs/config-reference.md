# 설정 참조

## 1. 적용 규칙

### 파일 위치

| 대상 | 경로 |
| --- | --- |
| 서버 기본 | `C:\AiCtrl\AgentServer\config\server.yaml` |
| 서버 오버라이드 | `C:\AiCtrl\AgentServer\config\server.overrides.yaml` |
| 서버 시크릿 | `C:\AiCtrl\AgentServer\config\secrets.bin` |
| 클라이언트 기본 | `C:\AiCtrl\Agent\config\client.yaml` |
| 클라이언트 오버라이드 | `C:\AiCtrl\Agent\config\client.overrides.yaml` |
| 클라이언트 시크릿 | `C:\AiCtrl\Agent\data\secrets.bin` |

서버 설정 경로는 `AcDefaultConfigs.DefaultServerConfigPath`에 하드코딩되어 있습니다. 실행 디렉터리나 명령행 인자로 바꿀 수 없습니다.

Agent 홈은 `AcDefaultConfigs.AgentHome`이 결정합니다. 기본값은 `C:\AiCtrl\Agent`이며 `AICTRL_AGENT_HOME` 환경변수로 덮어쓸 수 있습니다. 설정, 데이터, 로그는 모두 이 홈 아래에 배치됩니다.

### 병합 순서

낮은 우선순위에서 높은 우선순위 순서입니다.

1. 기본 YAML
2. `*.overrides.yaml` (선택)
3. `AICTRL__` 환경변수

환경변수는 `__`를 `:`로 바꾼 키로 매핑됩니다. 예를 들어 `AICTRL__auth__googleClientId`는 `auth:googleClientId`를 덮어씁니다. 시퀀스는 인덱스가 필요합니다.

```powershell
$env:AICTRL__auth__allowedEmails__0 = 'user@example.com'
$env:AICTRL__logging__fileLevel = 'Information'
```

### YAML 규칙

- 키는 대소문자를 구분하지 않습니다.
- 탭 들여쓰기와 다중 문서(`---`)는 거부됩니다.
- 빈 파일과 주석만 있는 파일은 허용됩니다.
- 값은 flat key로 변환됩니다. `allowedEmails: [a, b]`는 `allowedEmails:0`, `allowedEmails:1`이 됩니다.
- `configVersion: 1`이 기본값에 포함되어 있지만 런타임에서 검사하지 않습니다. 검증기(`AcConfigValidator`)는 테스트에서만 사용됩니다.
- 알 수 없는 키도 런타임에서 경고하지 않고 무시됩니다.
- 핫 리로드는 없습니다. `AcYamlConfigurationProvider`는 watch를 지원하지만 `AddAcYamlFile`이 watch를 켜지 않습니다.
- 값이 잘못되면 대부분 조용히 기본값으로 대체됩니다. 예외는 YAML 파싱 오류와 필수 파일 누락입니다.
- `client.overrides.yaml`은 워크스페이스 등록 시 클라이언트가 다시 씁니다. 주석과 원래 형식이 유지되지 않으므로 수동 편집은 기본 YAML에서 하는 편이 안전합니다. 재작성 시 `.bak` 파일이 남습니다.

## 2. 서버 설정

### 실제 소비되는 키

| 키 | 기본값 | 소비처 | 설명 |
| --- | --- | --- | --- |
| `kestrel:endpoints:<name>:url` | local 5080, edge 5081 | Kestrel | 엔드포인트 이름과 URL. 다른 Kestrel 키도 그대로 전달 |
| `agentHub:maximumReceiveMessageSize` | 2097152 | `GeoOptions` → SignalR | SignalR 최대 수신 바이트. 이름과 달리 GeoOptions에서 읽음 |
| `auth:googleClientId` | 빈 값 | `AuthOptions` | Google OAuth 클라이언트 ID |
| `auth:allowedEmails:<n>` | 빈 목록 | `AuthOptions` | 허용 이메일. 소문자로 정규화 |
| `auth:stepUpMinutes` | 30 | `AuthOptions` | 워크스페이스 등록 step-up 유효 시간 |
| `geo:dbPath` | `C:\AiCtrl\AgentServer\data\GeoLite2-Country.mmdb` | `GeoOptions` | MaxMind DB 경로 |
| `geo:allowCountries:<n>` | `[KR]` | `GeoOptions` | 허용 국가 코드. 비어 있으면 KR |
| `geo:allowCidrs:<n>` | 빈 목록 | `GeoOptions` | 국가 판별과 무관하게 허용할 CIDR |
| `geo:failClosed` | true | `GeoOptions` | 문자열 `false`일 때만 비활성. 그 외에는 true |
| `database:path` | `C:\AiCtrl\AgentServer\data\aictrl.db` | `ServerDbOptions` | SQLite 파일 |
| `web:distPath` | 없음 | `WebApp.ResolveDistPath` | SPA dist 경로. 유효하지 않으면 fallback 없이 UI 미제공 |
| `streams:ringChunks` | 2000 | `StreamOptions` | 세션 ring 최대 청크 수 (최소 1) |
| `streams:ringBytes` | 1048576 | `StreamOptions` | 세션 ring 최대 바이트 (최소 1024) |
| `streams:idleDropMinutes` | 10 | `StreamOptions` | 유휴 스트림 정리 기준 (최소 1분) |
| `streams:leaseSeconds` | 60 | `StreamOptions` | watch lease. 1..300으로 clamp |
| `streams:longPollMaxMs` | 25000 | `StreamOptions` | long-poll 최대 대기. 0..25000으로 clamp |
| `transfers:dir` | `C:\AiCtrl\AgentServer\data\transfers` | `TransferOptions` | 전송 임시 디렉터리 |
| `transfers:maxFileBytes` | 2147483648 | `TransferOptions` | 파일 최대 2GiB |
| `transfers:maxTotalBytes` | 21474836480 | `TransferOptions` | 전체 최대 20GiB |
| `transfers:chunkBytes` | 8388608 | `TransferOptions` | 청크 8MiB |
| `transfers:ttlHours` | 24 | `TransferOptions` | 전송 TTL |
| `requests:ackSeconds` | 10 | `RequestOptions` | ack 타임아웃 |
| `requests:commandSeconds` | 30 | `RequestOptions` | 기본 명령 응답 타임아웃 |
| `requests:querySeconds` | 10 | `RequestOptions` | 조회 타임아웃 |
| `requests:waitSeconds` | 10 | `RequestOptions` | 오프라인 대기. **정수만 유효** |
| `requests:maxAttempts` | 3 | `RequestOptions` | ack 최대 시도 |
| `logging:directory` | 실행 파일 디렉터리의 `logs` | `AcLogBootstrap` | 파일 로그 디렉터리. 절대 경로 권장 |
| `logging:fileLevel` | Debug | `AcLogBootstrap` | 파일 로그 최소 레벨 |
| `logging:consoleLevel` | Information | `AcLogBootstrap` | 콘솔 로그 최소 레벨 |
| `logging:retentionDays` | 30 | `AcLogBootstrap` | 보존 일수. 0이면 시작 시 기존 로그 전부 삭제 가능 |
| `logging:maxTotalGB` | 5 | `AcLogBootstrap` | 총 로그 크기. 0이면 전부 삭제 가능 |

환경변수 `AICTRL_LOG_DIR`는 `logging:directory`를 덮어씁니다. `logging:directory`가 없으면 실행 파일 디렉터리의 `logs`를 사용합니다. 단일 파일 최대 크기 500MiB는 설정 키가 없습니다.

### 무시되는 키

다음 키는 기본 YAML에 있지만 서버 코드에서 읽지 않습니다.

| 키 | 비고 |
| --- | --- |
| `tunnel:*` | Cloudflare metrics URL, protocol |
| `forwardedHeaders:*` | forwarded headers 미들웨어 미배선 |
| `commands:defaultReadTtlSeconds` | 명령 TTL은 코드에서 5분 고정 |
| `commands:defaultMutationTtlSeconds` | 위와 같음 |
| `commands:ackTimeoutSeconds` | `requests:ackSeconds`가 실제 값 |
| `queries:defaultTimeoutMs` | `requests:querySeconds`가 실제 값 |
| `queries:coalesceMs` | in-flight 직렬화는 즉시 수행 |
| `requests:offlineGraceSeconds` | `requests:waitSeconds` 사용 |
| `requests:ackRetry` | `requests:maxAttempts` 사용 |
| `agentHub:transports` | Agent Hub는 WebSockets 하드코딩 |
| `notifications:*` | VAPID, throttle |
| `backup:*` | 백업 hosted service 없음 |
| `configVersion` | 런타임 미검사 |

`requests.waitSeconds`는 기본 YAML에서 매핑이므로 정수 오버라이드가 없으면 기본값 10초로 대체됩니다.

```yaml
requests:
  waitSeconds: 20
```

### Kestrel 예시

```yaml
kestrel:
  endpoints:
    local:
      url: 'http://127.0.0.1:5080'
    edge:
      url: 'http://127.0.0.1:5081'
```

엔드포인트 이름은 자유롭게 추가할 수 있습니다. GeoGate는 `local`과 `edge`의 포트를 이름으로 찾습니다. 다른 이름만 사용하면 로컬/edge 구분이 기본값(5080/5081)으로 남습니다.

### 시크릿 키

`secrets.bin`은 `key<TAB>base64(value)` 라인을 DPAPI로 암호화합니다. 현재 코드가 읽는 키는 다음과 같습니다.

| 키 | 사용처 |
| --- | --- |
| `google-client-secret` | Google OAuth |
| `opencode.serve.password` | Agent가 OpenCode Basic auth에 사용 |

`secrets set`으로 임의 이름을 저장할 수 있지만, 코드가 읽지 않는 이름은 효과가 없습니다.

### 서버 오버라이드 예시

```yaml
configVersion: 1
logging:
  fileLevel: Information
requests:
  waitSeconds: 20
geo:
  allowCountries: [KR, JP]
```

## 3. 클라이언트 설정

### 실제 소비되는 키

| 키 | 기본값 | 소비처 | 설명 |
| --- | --- | --- | --- |
| `serverUrl` | `http://127.0.0.1:5080` | `ServerLinkOptions` | 서버 링크와 등록 기본 URL |
| `reconnect:maxBackoffSeconds` | 30 | `ServerLinkOptions` | 재연결 최대 백오프. 초기값은 1초 고정 |
| `localApi:port` | 47800 | `LocalApiOptions` | 0이면 OS가 배정. 0..65535 |
| `localApi:tokenPath` | `C:\AiCtrl\Agent\data\local-token` | `LocalApiOptions` | Bearer 토큰 파일 |
| `opencode:executable` | `opencode` | `ProviderHostOptions` | 실행 파일. PATH에 없으면 절대 경로 |
| `opencode:host` | `127.0.0.1` | `ProviderHostOptions` | OpenCode 바인딩 호스트 |
| `opencode:port` | 4096 | `ProviderHostOptions` | 1..65535 |
| `opencode:instanceId` | 없음 | `ProviderHostOptions` | 기본값은 endpoint authority |
| `monitor:statusPollSeconds` | 5 | `MonitorOptions` | 상태 polling 간격. 0 이하이면 기본값 |
| `monitor:stallMinutes` | 10 | `MonitorOptions` | stall 판정 시간 |
| `monitor:logReasoning` | true | `MonitorOptions` | reasoning 로그 여부 |
| `monitor:logToolOutputMaxKB` | 64 | `MonitorOptions` | 툴 출력 로그 최대 KB |
| `workspaceParents:<n>` | 빈 목록 | `WorkspaceOptions` | 신규 워크스페이스 등록 허용 부모 |
| `workspaces:<n>:name` | 경로 마지막 이름 | `WorkspaceOptions` | 표시 이름 |
| `workspaces:<n>:root` | 없음 | `WorkspaceOptions` | 루트 경로. 없으면 항목 무시 |
| `workspaces:<n>:allowUpload` | true | `WorkspaceOptions` | 업로드 허용 |
| `workspaces:<n>:allowDownload` | true | `WorkspaceOptions` | 다운로드 허용 |
| `workspaces:<n>:allowMkdir` | true | `WorkspaceOptions` | 디렉터리 생성 허용 |
| `workspaces:<n>:allowDelete` | false | `WorkspaceOptions` | 삭제·이름변경 허용 |
| `workspaces:<n>:denyPatterns:<m>` | 빈 목록 | `WorkspaceOptions` | 기본 deny 패턴에 추가 |
| `registeredWorkspaces:<n>:...` | 없음 | `WorkspaceOptions` | `workspaces`와 동일 구조. 클라이언트가 기록 |
| `vpn:enabled` | true | `VpnOptions` | VPN 기능 전체 on/off |
| `vpn:helperEnabled` | true | `VpnOptions` | helper 사용. false면 CLI 직접 실행 |
| `vpn:helperTokenPath` | `C:\AiCtrl\Agent\data\vpn-helper.token` | `VpnOptions` | helper 인증 토큰 |
| `vpn:cliPath` | 빈 값 | `VpnOptions` | ProtonVPN CLI 경로. 비우면 자동 탐색 |
| `vpn:connectTimeoutSeconds` | 90 | `VpnOptions` | 연결 타임아웃 |
| `vpn:queryTimeoutSeconds` | 20 | `VpnOptions` | 상태·목록 조회 타임아웃. 기본 YAML에 없음 |
| `vpn:statusPollSeconds` | 30 | `VpnOptions` | 상태 polling |
| `vpn:defaultCountry` | JP | `VpnOptions` | 기본 국가. 대문자 정규화 |
| `vpn:historyPath` | `C:\AiCtrl\Agent\data\vpn-history.json` | `VpnOptions` | 연결 이력. 기본 YAML에 없음 |
| `vpn:fallback:mode` | TunnelReady | `VpnOptions` | `TunnelReady` 또는 `ServerLink` |
| `vpn:fallback:readyUrl` | `http://127.0.0.1:20241/ready` | `VpnOptions` | TunnelReady 확인 URL |
| `vpn:fallback:minutes` | 5 | `VpnOptions` | fallback 최대 대기 |
| `vpn:recovery:enabled` | false | `VpnRecoveryOptions` | 자동 복구 기본값. 웹 값이 우선 |
| `vpn:recovery:discordWebhookUrl` | 빈 값 | `VpnRecoveryOptions` | 폴백 Discord webhook. HTTPS만 허용 |
| `vpn:recovery:discordEnabled` | false | `VpnRecoveryOptions` | Discord 알림 사용 |
| `vpn:recovery:discordNotifyOnFailureOnly` | false | `VpnRecoveryOptions` | 실패·시도 초과만 알림 |
| `vpn:recovery:autoConnectVpn` | true | `VpnRecoveryOptions` | 복구 시 VPN 연결 |
| `vpn:recovery:autoResume` | true | `VpnRecoveryOptions` | 복구 시 세션 재개 |
| `vpn:recovery:handleQuotaExceeded` | true | `VpnRecoveryOptions` | 무료 할당량 소진 처리 |
| `vpn:recovery:handleRateLimited` | true | `VpnRecoveryOptions` | RateLimited 처리 |
| `vpn:recovery:handleAuthFailed` | false | `VpnRecoveryOptions` | 인증 실패 처리 |
| `vpn:recovery:handleModelUnavailable` | false | `VpnRecoveryOptions` | 모델 사용 불가 처리 |
| `vpn:recovery:handleNetwork` | `handleStalled` 값 | `VpnRecoveryOptions` | 네트워크 오류 처리 |
| `vpn:recovery:handleStalled` | true | `VpnRecoveryOptions` | 정체 이벤트 처리 |
| `vpn:recovery:handleUnknown` | false | `VpnRecoveryOptions` | 알 수 없는 오류 처리 |
| `vpn:recovery:continuationPrompt` | 고정 문장 | `VpnRecoveryOptions` | 재개 프롬프트 |
| `vpn:recovery:country` | 없음 | `VpnRecoveryOptions` | 복구 국가 |
| `vpn:recovery:city` | 없음 | `VpnRecoveryOptions` | 복구 도시 |
| `vpn:recovery:servers:<n>` | 빈 목록 | `VpnRecoveryOptions` | 복구 서버 후보 |
| `vpn:recovery:cooldownSeconds` | 600 | `VpnRecoveryOptions` | 세션별 쿨다운 |
| `vpn:recovery:maxAttemptsPerSession` | 3 | `VpnRecoveryOptions` | 세션별 최대 시도 |
| `vpn:recovery:stallThresholdSeconds` | 0 | `VpnRecoveryOptions` | 최소 정체 시간. 0이면 모두 처리 |
| `logging:directory` 등 | 서버와 동일 | `AcLogBootstrap` | 클라이언트 로그. 기본 경로 `C:\AiCtrl\Agent\logs` |

### 무시되는 키

| 키 | 비고 |
| --- | --- |
| `opencode:mode` | 항상 헬스 프로브 후 채택 또는 기동 |
| `opencode:apiVersion` | `/doc`를 보고 자동 선택 |
| `monitor:processScanSeconds` | 프로세스 스캔은 별도 고정 주기 |
| `monitor:reconcileSeconds` | 미사용 |
| `stream:batchMs` | 스트림 배칭은 코드 고정 |
| `stream:batchBytes` | 위와 같음 |
| `stream:throttleBytesPerSec` | 위와 같음 |
| `stream:activeWindowHours` | 위와 같음 |
| `transfers:maxConcurrent` | 전송 동시성은 코드 고정 |
| `transfers:maxBytesPerSec` | 미사용 |
| `transfers:chunkTimeoutSeconds` | 서버 응답이 결정 |
| `transfers:maxRetries` | 재개 루프는 30초 고정 |
| `power:keepAwakeWhileBusy` | 미구현 |
| `errorRules` | 미구현 |
| `configVersion` | 런타임 미검사 |

`monitor`의 stall 검사 주기는 30초, `logToolInputMaxKb`는 2로 코드에 고정되어 있습니다.

### 워크스페이스 예시

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
    denyPatterns:
      - '*.log'
```

- `workspaces`와 `registeredWorkspaces`는 합쳐집니다.
- 같은 루트가 중복되면 먼저 나온 항목만 사용합니다.
- `workspaceParents`에 없는 경로는 `workspace.register` 명령으로 등록할 수 없습니다.
- 삭제와 이름변경은 모두 `allowDelete`를 사용합니다. 별도 `allowRename` 키는 없습니다.
- `denyPatterns`는 기본 목록에 추가되며 기본 목록을 제거할 수 없습니다.

### VPN 복구 예시

```yaml
vpn:
  enabled: true
  defaultCountry: JP
  fallback:
    mode: TunnelReady
    readyUrl: 'http://127.0.0.1:20241/ready'
    minutes: 5
  recovery:
    enabled: true
    discordEnabled: true
    discordNotifyOnFailureOnly: false
    discordWebhookUrl: 'https://discord.com/api/webhooks/...'
    autoConnectVpn: true
    autoResume: true
    handleQuotaExceeded: true
    handleRateLimited: true
    handleAuthFailed: false
    handleModelUnavailable: false
    handleNetwork: true
    handleStalled: true
    handleUnknown: false
    continuationPrompt: 'Continue from where you stopped. Do not repeat completed work.'
    country: 'JP'
    city: ''
    servers:
      - 'JP#1'
    cooldownSeconds: 600
    maxAttemptsPerSession: 3
    stallThresholdSeconds: 0
```

복구는 기본 비활성입니다. Discord webhook은 HTTPS이면서 Discord 호스트의 `/api/webhooks/` 경로여야 하며, 아니면 저장이 거부됩니다. 웹에서 저장한 값은 `vpn-policy.json`과 DPAPI secret store에 기록되고, 이 YAML 값은 파일이 없을 때의 기본값 또는 webhook 폴백으로만 사용됩니다.

### 클라이언트 오버라이드 예시

```yaml
configVersion: 1
opencode:
  executable: 'C:\Users\user\AppData\Roaming\npm\node_modules\@opencode\cli\bin\opencode.exe'
```

워크스페이스 등록은 이 파일에 `registeredWorkspaces` 항목을 추가합니다. 등록 명령이 실행 중일 때 파일을 직접 편집하면 변경이 유실될 수 있습니다.

## 4. 기본값과 실제 동작의 차이

| 항목 | 기본 YAML | 실제 동작 |
| --- | --- | --- |
| 명령 ack 타임아웃 | `commands.ackTimeoutSeconds: 10` | `requests.ackSeconds` 기본 10 |
| 명령 TTL | 60/300초 | 300초 고정 |
| 조회 타임아웃 | `queries.defaultTimeoutMs: 10000` | `requests.querySeconds` 기본 10 |
| 오프라인 대기 | `requests.waitSeconds` 매핑 | 정수 오버라이드 없으면 10초 |
| 명령 타입별 대기 | `requests.waitSeconds` 매핑 | 코드에 하드코딩된 표 사용 |
| OpenCode 모드 | `Managed` | 모드 무시, 항상 채택/기동 |
| 스트림 배칭 | `stream.*` | 코드 고정 |
| 전송 동시성 | `transfers.maxConcurrent: 2` | 코드 고정 |
| 절전 방지 | `power.keepAwakeWhileBusy: true` | 미구현 |
| 서버 백업 | `backup.*` | 미구현 |
| Cloudflare tunnel | `tunnel.*` | 미사용 |
| forwarded headers | `forwardedHeaders.*` | 미들웨어 미배선 |
| 알림 | `notifications.*` | 미구현 |

설정 키를 추가할 때는 `AcDefaultConfigs`에만 넣지 말고 옵션 클래스의 `FromConfiguration`과 실제 사용처를 함께 배선해야 합니다.

## 5. 검증

- YAML 파싱 오류는 `AcConfigException`으로 시작을 중단합니다.
- 탭, 다중 문서, 중복 키는 거부됩니다.
- `configVersion` 누락·미지원 값은 런타임에서 검사하지 않습니다.
- 알 수 없는 키는 무시됩니다.
- 숫자·불리언 파싱 실패는 기본값으로 대체됩니다.
- 경로가 잘못되어도 대부분 시작 시 생성 또는 무시되며, 실제 사용 시점에 실패합니다.
- `web.distPath`가 존재해도 `index.html`이 없으면 UI를 제공하지 않습니다.
