using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiCtrl.Client.Vpn;

public static class VpnHelperOperations
{
    public const string Ping = "ping";

    public const string Status = "status";

    public const string Countries = "countries";

    public const string Servers = "servers";

    public const string Connect = "connect";

    public const string Disconnect = "disconnect";
}

public sealed record VpnHelperCommand(string Operation, string? Argument);

public sealed record VpnHelperRequest(string Token, string Operation, string? Argument);

public sealed record VpnHelperResponse(int ExitCode, string StdOut, string StdErr, string? ProtocolError);

[JsonSerializable(typeof(VpnHelperRequest))]
[JsonSerializable(typeof(VpnHelperResponse))]
internal sealed partial class VpnHelperJsonContext : JsonSerializerContext;

public static partial class VpnHelperArguments
{
    public static bool TryParse(string arguments, out VpnHelperCommand command)
    {
        command = new VpnHelperCommand(string.Empty, null);
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        var value = arguments.Trim();
        if (string.Equals(value, "status --json", StringComparison.Ordinal))
        {
            command = new VpnHelperCommand(VpnHelperOperations.Status, null);
            return true;
        }

        if (string.Equals(value, "countries --json", StringComparison.Ordinal))
        {
            command = new VpnHelperCommand(VpnHelperOperations.Countries, null);
            return true;
        }

        if (string.Equals(value, "disconnect --json --yes", StringComparison.Ordinal))
        {
            command = new VpnHelperCommand(VpnHelperOperations.Disconnect, null);
            return true;
        }

        var servers = ServersPattern().Match(value);
        if (servers.Success)
        {
            command = new VpnHelperCommand(
                VpnHelperOperations.Servers,
                servers.Groups[1].Value.ToUpperInvariant());
            return true;
        }

        var connect = ConnectPattern().Match(value);
        if (connect.Success)
        {
            command = new VpnHelperCommand(VpnHelperOperations.Connect, connect.Groups[1].Value);
            return true;
        }

        return false;
    }

    public static string ToCliArguments(VpnHelperCommand command) =>
        command.Operation switch
        {
            VpnHelperOperations.Status => "status --json",
            VpnHelperOperations.Countries => "countries --json",
            VpnHelperOperations.Servers => $"servers --country {command.Argument} --json --limit 2000",
            VpnHelperOperations.Connect => $"connect {command.Argument} --json --yes",
            VpnHelperOperations.Disconnect => "disconnect --json --yes",
            _ => throw new ArgumentOutOfRangeException(nameof(command), command.Operation, "Unsupported VPN helper operation."),
        };

    [GeneratedRegex("^servers --country ([A-Za-z]{2}) --json --limit 2000$")]
    private static partial Regex ServersPattern();

    [GeneratedRegex("^connect ([A-Za-z0-9][A-Za-z0-9#._-]{0,63}) --json --yes$")]
    private static partial Regex ConnectPattern();
}
