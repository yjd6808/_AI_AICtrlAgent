using AiCtrl.Core.Config;
using AiCtrl.Core.Logging;
using AiCtrl.Core.Runtime;

var builder = WebApplication.CreateBuilder(args);

var serverConfigPath = AcDefaultConfigs.DefaultServerConfigPath;
try
{
    AcDefaultConfigs.EnsureFile(serverConfigPath, AcDefaultConfigs.ServerYaml);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"설정 파일 준비 실패: {serverConfigPath} — {ex.Message}");
    return 2;
}

builder.Configuration.AddAcYamlFile(serverConfigPath);
builder.Configuration.AddAcYamlFile(AcDefaultConfigs.OverridesPathFor(serverConfigPath), optional: true);
builder.Configuration.AddAcEnvironmentVariables();

var logOptions = AcLogBootstrap.ReadOptions(builder.Configuration);
AcLog.Initialize("Server", logOptions);
builder.Logging.AddAcLog(logOptions);

builder.Services.AddAcRuntime();

var app = builder.Build();

app.MapGet("/", () => "AI-Ctrl-Agent Server");

app.Run();
return 0;
