using AiCtrl.Client.Commands;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Systems;
using AiCtrl.Core.Config;
using AiCtrl.Core.Logging;
using AiCtrl.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

var ct = CancellationToken.None;
var time = TimeProvider.System;
ClientPaths.EnsureDataDirectory();

var clientDb = new ClientDb(ClientPaths.DatabasePath, time);
await clientDb.InitializeAsync(ct);
var machineId = await clientDb.GetOrCreateMachineIdAsync(ct);
var journal = new JournalStore(clientDb);
await journal.RecoverInterruptedAsync(time.GetUtcNow(), ct);

var registry = new ProviderInstanceRegistry(machineId);
var monitorOptions = MonitorOptions.FromConfiguration(builder.Configuration);
var providerHostOptions = ProviderHostOptions.FromConfiguration(builder.Configuration);
var localApiOptions = LocalApiOptions.FromConfiguration(builder.Configuration);

builder.Services.AddSingleton(clientDb);
builder.Services.AddSingleton(journal);
builder.Services.AddSingleton<IProviderInstanceRegistry>(registry);
builder.Services.AddSingleton(sp => new SessionActorPool(sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton(sp => new AbortAndPromptRunner(
    AbortAndPromptOptions.Default,
    time,
    sp.GetRequiredService<ILogger<AbortAndPromptRunner>>()));
builder.Services.AddSingleton(sp => new CommandSystem(
    journal,
    sp.GetRequiredService<SessionActorPool>(),
    registry,
    sp.GetRequiredService<AbortAndPromptRunner>(),
    time,
    sp.GetRequiredService<ILogger<CommandSystem>>()));

builder.Services.AddSingleton(sp => new ProviderHostSystem(
    registry,
    providerHostOptions,
    time,
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton(sp => new MonitorSystem(
    registry,
    monitorOptions,
    time,
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton(sp => new StateSystem(
    time,
    monitorOptions,
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton(sp => new LocalApiSystem(
    sp.GetRequiredService<CommandSystem>(),
    registry,
    sp.GetRequiredService<StateSystem>(),
    localApiOptions,
    time,
    sp.GetRequiredService<ILoggerFactory>()));

builder.Services.AddSingleton<IAcSystem>(sp => sp.GetRequiredService<ProviderHostSystem>());
builder.Services.AddSingleton<IAcSystem>(sp => sp.GetRequiredService<MonitorSystem>());
builder.Services.AddSingleton<IAcSystem>(sp => sp.GetRequiredService<StateSystem>());
builder.Services.AddSingleton<IAcSystem>(sp => sp.GetRequiredService<LocalApiSystem>());

var host = builder.Build();
await host.RunAsync();
return 0;
