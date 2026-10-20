using AiCtrl.Server;
using AiCtrl.Server.Security;

if (ServerSecretsCli.IsCommand(args))
{
    return ServerSecretsCli.Run(args);
}

try
{
    var app = ServerSetup.BuildAcServer(args);
    await app.RunAsync().ConfigureAwait(false);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Server 시작 실패: {ex.Message}");
    return 1;
}
