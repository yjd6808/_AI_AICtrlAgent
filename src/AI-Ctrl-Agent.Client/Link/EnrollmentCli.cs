using System.Runtime.InteropServices;
using AiCtrl.Client.Security;
using AiCtrl.Client.Storage;
using AiCtrl.Core.Protocol;
using Microsoft.Extensions.Configuration;

namespace AiCtrl.Client.Link;

public static class EnrollmentCli
{
    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);

        var server = ValueAfter(args, "--server") ?? configuration["serverUrl"] ?? "http://127.0.0.1:5080";
        var code = ValueAfter(args, "--code") ?? args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(code))
        {
            Console.Error.WriteLine("사용법: AI-Ctrl-Agent.Client enroll --code ABCD-1234 [--server http://host:5080]");
            return 2;
        }

        ClientPaths.EnsureDataDirectory();
        var db = new ClientDb(ClientPaths.DatabasePath);
        await db.InitializeAsync(ct).ConfigureAwait(false);
        var secrets = new DpapiSecretStore(ClientPaths.SecretsPath);
        var keys = new DeviceKeyStore(secrets);
        var key = await keys.GetOrCreateAsync(ct).ConfigureAwait(false);
        using var api = new ServerApiClient(server);
        var request = new EnrollmentRequest(
            code,
            key.PublicKey,
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            typeof(EnrollmentCli).Assembly.GetName().Version?.ToString() ?? "0.0.0");
        try
        {
            var response = await api.EnrollAsync(request, ct).ConfigureAwait(false);
            await db.SetMachineIdAsync(response.MachineId, enrolled: true, ct).ConfigureAwait(false);
            await db.SetValueAsync("server.url", api.BaseUrl, ct).ConfigureAwait(false);
            Console.WriteLine($"등록 완료: {response.MachineId.Value}");
            return 0;
        }
        catch (AcApiException ex)
        {
            Console.Error.WriteLine($"등록 실패: {ex.Code} - {ex.Message}");
            return 1;
        }
    }

    private static string? ValueAfter(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
