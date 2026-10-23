using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Tests.Support;

/// <summary>把所有日志收集起来，用于检查日志里有没有泄露敏感信息。</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public string AllText => string.Join('\n', Lines);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            owner.Lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
        }
    }
}
