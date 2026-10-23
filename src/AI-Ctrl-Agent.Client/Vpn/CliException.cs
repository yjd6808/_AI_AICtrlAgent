namespace AiCtrl.Client.Vpn;

public sealed class CliException : Exception
{
    public int ExitCode { get; }

    public CliException(string message, int exitCode)
        : base(message)
    {
        ExitCode = exitCode;
    }
}

public sealed class VpnException : Exception
{
    public string Code { get; }

    public VpnException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}

public static class CliExitCode
{
    public const int Ok = 0;

    public const int Error = 1;

    public const int Usage = 2;

    public const int NotSignedIn = 3;

    public const int Privilege = 4;
}
