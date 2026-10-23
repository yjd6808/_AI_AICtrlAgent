using System.Text;

namespace AiCtrl.Core.Config;

public static class AcDefaultConfigs
{
    public static string DefaultServerConfigPath { get; } = @"C:\AiCtrl\Server\config\server.yaml";

    public static string DefaultClientConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AiCtrl",
        "client.yaml");

    public static bool EnsureFile(string filePath, string defaultContent)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(defaultContent);

        var fullPath = Path.GetFullPath(filePath);
        if (File.Exists(fullPath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, defaultContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    public static string OverridesPathFor(string configPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(configPath);

        var fullPath = Path.GetFullPath(configPath);
        var directory = Path.GetDirectoryName(fullPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(fullPath);
        return Path.Combine(directory, name + ".overrides.yaml");
    }

    public static string ServerYaml { get; } =
        """
        configVersion: 1

        kestrel:
          endpoints:
            local:
              url: 'http://127.0.0.1:5080'
            edge:
              url: 'http://127.0.0.1:5081'

        tunnel:
          metricsUrl: 'http://127.0.0.1:20241'
          protocol: auto

        auth:
          googleClientId: ''
          allowedEmails: []
          stepUpMinutes: 30

        geo:
          dbPath: 'C:\AiCtrl\Server\data\GeoLite2-Country.mmdb'
          allowCountries: [KR]
          allowCidrs: []
          failClosed: true

        forwardedHeaders:
          mode: CloudflareTunnel
          edgeEndpointOnly: true

        commands:
          defaultReadTtlSeconds: 60
          defaultMutationTtlSeconds: 300
          ackTimeoutSeconds: 10

        queries:
          defaultTimeoutMs: 10000
          coalesceMs: 2000

        requests:
          offlineGraceSeconds: 10
          ackRetry: 3
          waitSeconds:
            query: 10
            session.abort: 15
            session.prompt: 15
            session.abort_and_prompt: 30
            vpn.connect: 120
            vpn.disconnect: 30
            default: 30

        agentHub:
          transports: WebSockets
          maximumReceiveMessageSize: 2097152

        streams:
          ringChunks: 2000
          ringBytes: 1048576
          idleDropMinutes: 10
          leaseSeconds: 60
          longPollMaxMs: 25000

        transfers:
          dir: 'C:\AiCtrl\Server\data\transfers'
          maxFileBytes: 2147483648
          maxTotalBytes: 21474836480
          chunkBytes: 8388608
          ttlHours: 24

        notifications:
          vapidSubject: ''
          throttleMinutes: 5

        logging:
          fileLevel: Debug
          consoleLevel: Information
          retentionDays: 30
          maxTotalGB: 5

        database:
          path: 'C:\AiCtrl\Server\data\aictrl.db'

        backup:
          dir: 'C:\AiCtrl\Server\data\backup'
          keep: 14
        """;

    public static string ClientYaml { get; } =
        """
        configVersion: 1

        serverUrl: 'http://127.0.0.1:5080'

        localApi:
          port: 47800

        opencode:
          mode: Managed
          executable: opencode
          host: 127.0.0.1
          port: 4096
          apiVersion: auto

        monitor:
          statusPollSeconds: 5
          processScanSeconds: 30
          reconcileSeconds: 60
          stallMinutes: 10
          logReasoning: true
          logToolOutputMaxKB: 64

        stream:
          batchMs: 100
          batchBytes: 8192
          throttleBytesPerSec: 65536
          activeWindowHours: 24

        workspaceParents: []

        workspaces: []

        transfers:
          maxConcurrent: 2
          maxBytesPerSec: 0
          chunkTimeoutSeconds: 120
          maxRetries: 5

        power:
          keepAwakeWhileBusy: true

        errorRules: []

        vpn:
          enabled: true
          helperEnabled: true
          helperTokenPath: ''
          cliPath: ''
          connectTimeoutSeconds: 90
          statusPollSeconds: 30
          defaultCountry: JP
          fallback:
            mode: TunnelReady
            readyUrl: 'http://127.0.0.1:20241/ready'
            minutes: 5
          recovery:
            enabled: false
            discordWebhookUrl: ''
            autoConnectVpn: true
            autoResume: true
            handleRateLimited: true
            handleStalled: true
            continuationPrompt: 'Continue from where you stopped. Do not repeat completed work.'
            country: ''
            city: ''
            servers: []
            cooldownSeconds: 600
            maxAttemptsPerSession: 3

        reconnect:
          maxBackoffSeconds: 30

        logging:
          fileLevel: Debug
          consoleLevel: Information
          retentionDays: 30
          maxTotalGB: 5
        """;
}
