using AiCtrl.ApiClient;
using AiCtrl.ClientMCP;
using AiCtrl.Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

AcLog.Initialize("ClientMCP");

var builder = Host.CreateApplicationBuilder(args);
var logOptions = AcLogBootstrap.ReadOptions(builder.Configuration);
builder.Logging.ClearProviders();
builder.Logging.AddAcLog(logOptions);

builder.Services.AddSingleton(_ => LocalApiClient.CreateFromEnvironment());

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(LocalTools).Assembly);

var host = builder.Build();
await host.RunAsync();
return 0;
