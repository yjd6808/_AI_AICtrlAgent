using AiCtrl.Core.Config;
using AiCtrl.Core.Logging;
using AiCtrl.Core.Runtime;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

var clientConfigPath = AcDefaultConfigs.DefaultClientConfigPath;
try
{
    AcDefaultConfigs.EnsureFile(clientConfigPath, AcDefaultConfigs.ClientYaml);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"설정 파일 준비 실패: {clientConfigPath} — {ex.Message}");
    return 2;
}

builder.Configuration.AddAcYamlFile(clientConfigPath);
builder.Configuration.AddAcYamlFile(AcDefaultConfigs.OverridesPathFor(clientConfigPath), optional: true);
builder.Configuration.AddAcEnvironmentVariables();

var logOptions = AcLogBootstrap.ReadOptions(builder.Configuration);
AcLog.Initialize("Client", logOptions);
builder.Logging.AddAcLog(logOptions);

builder.Services.AddAcRuntime();

var host = builder.Build();
await host.RunAsync();
return 0;
