using AiCtrl.Core.Logging;
using AiCtrl.Core.Runtime;
using Microsoft.Extensions.Hosting;

AcLog.Initialize("ServerMCP");

var builder = Host.CreateApplicationBuilder(args);
var logOptions = AcLogBootstrap.ReadOptions(builder.Configuration);
builder.Logging.AddAcLog(logOptions);
builder.Services.AddAcRuntime();

var host = builder.Build();
await host.RunAsync();
return 0;
