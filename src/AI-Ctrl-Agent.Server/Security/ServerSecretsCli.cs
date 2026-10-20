using System.Security.Cryptography;
using AiCtrl.Core.Config;

namespace AiCtrl.Server.Security;

public static class ServerSecretsCli
{
    public const string GoogleClientSecretName = "google-client-secret";

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "secrets", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        if (args.Length != 3 || !string.Equals(args[1], "set", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("사용법: AI-Ctrl-Agent.Server.exe secrets set <name>");
            Console.Error.WriteLine($"예: AI-Ctrl-Agent.Server.exe secrets set {GoogleClientSecretName}");
            return 2;
        }

        var name = args[2].Trim();
        if (name.Length == 0)
        {
            Console.Error.WriteLine("비밀 이름이 비어 있습니다.");
            return 2;
        }

        var value = ReadHidden($"값 입력 ({name}): ");
        if (string.IsNullOrWhiteSpace(value))
        {
            Console.Error.WriteLine("값이 비어 있습니다.");
            return 1;
        }

        try
        {
            var store = new DpapiServerSecretStore(DefaultPath);
            store.Set(name, value);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"비밀 저장 실패: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"저장 완료: {name} -> {DefaultPath}");
        return 0;
    }

    public static string DefaultPath { get; } = Path.Combine(
        Path.GetDirectoryName(AcDefaultConfigs.DefaultServerConfigPath) ?? ".",
        ServerSetup.SecretsFileName);

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? string.Empty;
        }

        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string(chars.ToArray());
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                {
                    chars.RemoveAt(chars.Count - 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                chars.Add(key.KeyChar);
            }
        }
    }
}
