using AiCtrl.Client.Commands;
using AiCtrl.Client.Link;
using AiCtrl.Client.Providers.OpenCode.Backend;
using AiCtrl.Client.Security;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Systems;
using AiCtrl.Client.Transfers;
using AiCtrl.Client.Vpn;
using AiCtrl.Client.Workspace;
using AiCtrl.Core.Config;
using AiCtrl.Core.Logging;
using AiCtrl.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder();

var clientConfigPath = AcDefaultConfigs.DefaultClientConfigPath;
try
{
    AcDefaultConfigs.EnsureFile(clientConfigPath, AcDefaultConfigs.ClientYaml);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"설정 파일 준비 실패: {clientConfigPath} - {ex.Message}");
    return 2;
}

builder.Configuration.AddAcYamlFile(clientConfigPath);
builder.Configuration.AddAcYamlFile(AcDefaultConfigs.OverridesPathFor(clientConfigPath), optional: true);
builder.Configuration.AddAcEnvironmentVariables();

var logOptions = AcLogBootstrap.ReadOptions(builder.Configuration);
AcLog.Initialize("Client", logOptions);
builder.Logging.AddAcLog(logOptions);

if (args.Length > 0 && string.Equals(args[0], "--vpn-helper", StringComparison.OrdinalIgnoreCase))
{
    using var helperCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        helperCts.Cancel();
    };
    var helperOptions = VpnOptions.FromConfiguration(builder.Configuration);
    return await VpnHelperHost.RunConsoleAsync(helperOptions, helperCts.Token).ConfigureAwait(false);
}

if (args.Length > 0 && string.Equals(args[0], "enroll", StringComparison.OrdinalIgnoreCase))
{
    return await EnrollmentCli.RunAsync(args.Skip(1).ToArray(), builder.Configuration, CancellationToken.None).ConfigureAwait(false);
}

builder.Services.AddAcRuntime();

var ct = CancellationToken.None;
var time = TimeProvider.System;
ClientPaths.EnsureDataDirectory();

var clientDb = new ClientDb(ClientPaths.DatabasePath, time);
await clientDb.InitializeAsync(ct).ConfigureAwait(false);
var machineId = await clientDb.IsEnrolledAsync(ct).ConfigureAwait(false)
    ? await clientDb.GetMachineIdAsync(ct).ConfigureAwait(false) ?? await clientDb.GetOrCreateMachineIdAsync(ct).ConfigureAwait(false)
    : await clientDb.GetOrCreateMachineIdAsync(ct).ConfigureAwait(false);
var journal = new JournalStore(clientDb);
await journal.RecoverInterruptedAsync(time.GetUtcNow(), ct).ConfigureAwait(false);

var registry = new ProviderInstanceRegistry(machineId);
var workspaceOptions = WorkspaceOptions.FromConfiguration(builder.Configuration);
var workspaceRegistry = new WorkspaceRegistry(workspaceOptions);
var filesSystem = new FilesSystem(workspaceRegistry);
var workspaceRegistrations = new WorkspaceRegistrationStore(
    AcDefaultConfigs.OverridesPathFor(clientConfigPath));
var transferLink = new TransferLinkState();
var monitorOptions = MonitorOptions.FromConfiguration(builder.Configuration);
var providerHostOptions = ProviderHostOptions.FromConfiguration(builder.Configuration);
var localApiOptions = LocalApiOptions.FromConfiguration(builder.Configuration);
var serverLinkOptions = ServerLinkOptions.FromConfiguration(builder.Configuration);
var vpnOptions = VpnOptions.FromConfiguration(builder.Configuration);
var vpnRecoveryOptions = VpnRecoveryOptions.FromConfiguration(builder.Configuration);
var vpnHelper = vpnOptions.HelperEnabled ? new VpnHelperClient(vpnOptions.HelperTokenPath) : null;
var serverLinkPresence = new ServerLinkPresence();
var secrets = new DpapiSecretStore(ClientPaths.SecretsPath);
var deviceKeys = new DeviceKeyStore(secrets);
var outbox = new OutboxStore(clientDb, time);
var network = new NetworkChangeSignal();

builder.Services.AddSingleton(clientDb);
builder.Services.AddSingleton(journal);
builder.Services.AddSingleton(workspaceOptions);
builder.Services.AddSingleton(workspaceRegistry);
builder.Services.AddSingleton(filesSystem);
builder.Services.AddSingleton(transferLink);
builder.Services.AddSingleton(sp => new TransferSystem(
    clientDb,
    workspaceRegistry,
    transferLink,
    time,
    sp.GetRequiredService<ILogger<TransferSystem>>()));
builder.Services.AddSingleton<ISecretStore>(secrets);
builder.Services.AddSingleton(deviceKeys);
builder.Services.AddSingleton(outbox);
builder.Services.AddSingleton(serverLinkOptions);
builder.Services.AddSingleton(vpnOptions);
builder.Services.AddSingleton(vpnRecoveryOptions);
builder.Services.AddSingleton(sp => new VpnPolicyStore(
    time,
    sp.GetRequiredService<ILogger<VpnPolicyStore>>()));
builder.Services.AddSingleton(serverLinkPresence);
builder.Services.AddSingleton<INetworkChangeSignal>(network);
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
    filesSystem,
    sp.GetRequiredService<TransferSystem>(),
    time,
    sp.GetRequiredService<ILogger<CommandSystem>>(),
    workspaceRegistrations,
    sp.GetRequiredService<VpnSystem>()));

builder.Services.AddSingleton(sp => new ProviderHostSystem(
    registry,
    providerHostOptions,
    sp.GetRequiredService<ISecretStore>(),
    time,
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<ProviderEventBroker>();
builder.Services.AddSingleton<AgentPushChannel>();
builder.Services.AddSingleton(sp => new MonitorSystem(
    registry,
    monitorOptions,
    time,
    sp.GetRequiredService<ILoggerFactory>(),
    events: sp.GetRequiredService<ProviderEventBroker>()));
builder.Services.AddSingleton(sp => new StreamSystem(
    registry,
    sp.GetRequiredService<ProviderEventBroker>(),
    sp.GetRequiredService<AgentPushChannel>(),
    time,
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton(sp => new StateSystem(
    time,
    monitorOptions,
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton(sp => new VpnSystem(
    vpnOptions,
    outbox,
    time,
    sp.GetRequiredService<ILogger<VpnSystem>>(),
    sp.GetRequiredService<StateSystem>(),
    helper: vpnHelper,
    serverLinkConnected: () => serverLinkPresence.IsConnected,
    policyStore: sp.GetRequiredService<VpnPolicyStore>()));
builder.Services.AddSingleton<IVpnRecoveryNotifier>(sp => new DiscordVpnRecoveryNotifier(
    vpnRecoveryOptions,
    sp.GetRequiredService<ILogger<DiscordVpnRecoveryNotifier>>()));
builder.Services.AddSingleton<IVpnRecoveryActions>(sp => new VpnRecoveryActions(
    sp.GetRequiredService<VpnSystem>(),
    registry,
    sp.GetRequiredService<AbortAndPromptRunner>(),
    vpnRecoveryOptions,
    sp.GetRequiredService<VpnPolicyStore>(),
    time,
    sp.GetRequiredService<ILogger<VpnRecoveryActions>>()));
builder.Services.AddSingleton(sp => new VpnRecoverySystem(
    sp.GetRequiredService<IVpnRecoveryActions>(),
    sp.GetRequiredService<IVpnRecoveryNotifier>(),
    vpnRecoveryOptions,
    sp.GetRequiredService<ILogger<VpnRecoverySystem>>()));
builder.Services.AddSingleton(sp => new LocalApiSystem(
    sp.GetRequiredService<CommandSystem>(),
    registry,
    sp.GetRequiredService<StateSystem>(),
    filesSystem,
    localApiOptions,
    time,
    sp.GetRequiredService<ILoggerFactory>(),
    () => serverLinkPresence.IsConnected));
builder.Services.AddSingleton(sp => new ClientRequestHandler(
    sp.GetRequiredService<CommandSystem>(),
    journal,
    registry,
    sp.GetRequiredService<StateSystem>(),
    filesSystem,
    time,
    sp.GetRequiredService<ILogger<ClientRequestHandler>>(),
    sp.GetRequiredService<VpnSystem>()));
builder.Services.AddSingleton(sp => new ServerLinkSystem(
    clientDb,
    sp.GetRequiredService<DeviceKeyStore>(),
    sp.GetRequiredService<OutboxStore>(),
    sp.GetRequiredService<ClientRequestHandler>(),
    sp.GetRequiredService<StreamSystem>(),
    sp.GetRequiredService<AgentPushChannel>(),
    transferLink,
    serverLinkOptions,
    sp.GetRequiredService<INetworkChangeSignal>(),
    time,
    sp.GetRequiredService<ILogger<ServerLinkSystem>>(),
    serverLinkPresence));

builder.Services.AddSingleton<IAcSystem>(sp => sp.GetRequiredService<ProviderHostSystem>());
builder.Services.AddSingleton<IAcSystem>(sp => sp.GetRequiredService<MonitorSystem>());
builder.Services.AddSingleton<IAcSystem>(sp => sp.GetRequiredService<StreamSystem>());
builder.Services.AddSingleton<IAcSystem>(sp => sp.GetRequiredService<StateSystem>());
builder.Services.AddSingleton<IAcSystem>(sp => sp.GetRequiredService<VpnSystem>());
builder.Services.AddSingleton<IAcSystem>(sp => sp.GetRequiredService<VpnRecoverySystem>());
builder.Services.AddSingleton<IAcSystem>(sp => sp.GetRequiredService<LocalApiSystem>());
builder.Services.AddSingleton<IAcSystem>(sp => sp.GetRequiredService<ServerLinkSystem>());
builder.Services.AddSingleton<IAcSystem>(sp => sp.GetRequiredService<TransferSystem>());

var host = builder.Build();
await host.RunAsync();
return 0;
