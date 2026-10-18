using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Core.Logging;

public sealed class AcFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, AcFileLogger> _loggers = new(StringComparer.Ordinal);
    private IExternalScopeProvider? _scopeProvider;

    public AcFileLoggerProvider(AcLogService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        Service = service;
    }

    public AcLogService Service { get; }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new AcFileLogger(this, name));

    public void Dispose() => _loggers.Clear();

    void ISupportExternalScope.SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    internal IExternalScopeProvider? ScopeProvider => _scopeProvider;

    private sealed class AcFileLogger : ILogger
    {
        private readonly AcFileLoggerProvider _provider;
        private readonly string _category;

        public AcFileLogger(AcFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= _provider.Service.Options.FileLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);
            var message = formatter(state, exception) ?? string.Empty;
            _provider.Service.Write(logLevel, _category, message, exception, BuildScopeSuffix());
        }

        private string BuildScopeSuffix()
        {
            var scopeProvider = _provider.ScopeProvider;
            if (scopeProvider is null)
            {
                return string.Empty;
            }

            var pairs = new List<string>();
            var plain = new StringBuilder();

            scopeProvider.ForEachScope(
                static (scope, state) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object>> keyValues)
                    {
                        foreach (var pair in keyValues)
                        {
                            state.Pairs.Add($"{pair.Key}={pair.Value}");
                        }
                    }
                    else if (scope is not null)
                    {
                        if (state.Plain.Length > 0)
                        {
                            state.Plain.Append(' ');
                        }

                        state.Plain.Append(scope);
                    }
                },
                (Pairs: pairs, Plain: plain));

            var builder = new StringBuilder();
            if (pairs.Count > 0)
            {
                builder.Append('{').Append(string.Join(' ', pairs)).Append('}');
            }

            if (plain.Length > 0)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(plain);
            }

            return builder.ToString();
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
