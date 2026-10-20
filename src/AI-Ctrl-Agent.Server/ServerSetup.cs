using AiCtrl.Core.Config;
using AiCtrl.Core.Logging;
using AiCtrl.Core.Runtime;
using AiCtrl.Core.Serialization;
using AiCtrl.Server.Endpoints;
using AiCtrl.Server.Hubs;
using AiCtrl.Server.Persistence;
using AiCtrl.Server.Requests;
using AiCtrl.Server.Security;
using AiCtrl.Server.Streams;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiCtrl.Server;

public static class ServerSetup
{
    public const string SecretsFileName = "secrets.bin";

    public static string DefaultSecretsPath => ServerSecretsCli.DefaultPath;

    public static IServiceCollection AddAcServer(
        this IServiceCollection services,
        ServerDbOptions dbOptions,
        GeoOptions geoOptions,
        AuthOptions authOptions,
        IServerSecretStore secrets,
        RequestOptions? requestOptions = null,
        StreamOptions? streamOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dbOptions);
        ArgumentNullException.ThrowIfNull(geoOptions);
        ArgumentNullException.ThrowIfNull(authOptions);
        ArgumentNullException.ThrowIfNull(secrets);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(secrets);
        services.AddSingleton(dbOptions);
        services.AddDbContextFactory<AcDbContext>(options => options.UseSqlite(dbOptions.ConnectionString));
        services.AddSingleton<ServerDb>();
        services.AddAcSystem<ServerStorageSystem>();
        services.AddSingleton(geoOptions);
        services.AddSingleton<IGeoIpLookup>(_ => new MaxMindGeoIpLookup(geoOptions.DbPath));
        services.AddSingleton<ChallengeStore>();
        services.AddSingleton<AgentTokenService>();
        services.AddSingleton<AgentAuthService>();
        services.AddSingleton<EnrollmentService>();
        services.AddSingleton<ConnectionRegistry>();
        services.AddSingleton(requestOptions ?? new RequestOptions());
        services.AddSingleton<PendingRequestTable>();
        services.AddSingleton<RequestDispatchService>();
        services.AddSingleton<RemoteQueryService>();
        services.AddSingleton<CommandExecutionService>();
        services.AddSingleton(streamOptions ?? new StreamOptions());
        services.AddSingleton<SessionStreamHub>();
        services.AddHostedService<StreamIdleSweeper>();
        services.AddAcAuthentication(authOptions, secrets);
        services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = geoOptions.MaximumReceiveMessageSize;
            options.EnableDetailedErrors = false;
        });
        services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AcJsonContext.Default);
        });
        services.AddAcRateLimiting();
        return services;
    }

    public static IServiceCollection AddAcDefaultServer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var dbOptions = ServerDbOptions.FromConfiguration(configuration);
        var geoOptions = GeoOptions.FromConfiguration(configuration);
        var authOptions = AuthOptions.FromConfiguration(configuration);
        var secrets = new DpapiServerSecretStore(DefaultSecretsPath);
        return services.AddAcServer(
            dbOptions,
            geoOptions,
            authOptions,
            secrets,
            RequestOptions.FromConfiguration(configuration),
            StreamOptions.FromConfiguration(configuration));
    }

    public static WebApplication BuildAcServer(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = WebApplication.CreateBuilder(args);
        var serverConfigPath = AcDefaultConfigs.DefaultServerConfigPath;
        try
        {
            AcDefaultConfigs.EnsureFile(serverConfigPath, AcDefaultConfigs.ServerYaml);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"?ㅼ젙 ?뚯씪 以鍮??ㅽ뙣: {serverConfigPath} - {ex.Message}");
            throw;
        }

        builder.Configuration.AddAcYamlFile(serverConfigPath);
        builder.Configuration.AddAcYamlFile(AcDefaultConfigs.OverridesPathFor(serverConfigPath), optional: true);
        builder.Configuration.AddAcEnvironmentVariables();

        var logOptions = AcLogBootstrap.ReadOptions(builder.Configuration);
        AcLog.Initialize("Server", logOptions);
        builder.Logging.AddAcLog(logOptions);
        builder.WebHost.ConfigureKestrel(options => options.Configure(builder.Configuration.GetSection("kestrel")));

        builder.Services.AddAcRuntime();
        builder.Services.AddAcDefaultServer(builder.Configuration);
        var webDist = WebApp.ResolveDistPath(builder.Configuration, builder.Environment.ContentRootPath);

        var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<GeoGateMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseAcWebApp(webDist);
        app.MapAcServerEndpoints();
        app.MapHub<AgentHub>("/hubs/agent", options =>
        {
            options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
        });
        app.MapHub<UiHub>("/hubs/ui");
        app.MapGet("/", () => "AI-Ctrl-Agent Server");
        return app;
    }
}
