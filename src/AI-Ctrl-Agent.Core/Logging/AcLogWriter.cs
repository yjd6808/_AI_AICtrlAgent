using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Core.Logging;

internal sealed class AcLogWriter
{
    private const int ChannelCapacity = 100_000;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);

    private readonly string _directory;
    private readonly string _baseName;
    private readonly long _maxFileBytes;
    private readonly Channel<LogEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly object _lock = new();
    private FileStream? _stream;
    private StreamWriter? _writer;
    private int _partIndex = 1;
    private long _bytesInPart;
    private long _lastFlushTimestamp;
    private bool _closed;
    private long _dropped;

    public AcLogWriter(string directory, string baseName, long maxFileBytes)
    {
        _directory = directory;
        _baseName = baseName;
        _maxFileBytes = maxFileBytes > 0 ? maxFileBytes : long.MaxValue;
        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        OpenPartLocked();
        _lastFlushTimestamp = Stopwatch.GetTimestamp();
        _thread = new Thread(ThreadBody)
        {
            IsBackground = true,
            Name = "AcLogWriter",
        };
        _thread.Start();
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public void Post(LogLevel level, string text)
    {
        var entry = new LogEntry(level, text);

        if (_channel.Writer.TryWrite(entry))
        {
            return;
        }

        if (level <= LogLevel.Debug)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        try
        {
            _channel.Writer.WriteAsync(entry).AsTask().GetAwaiter().GetResult(); // VERIFY(G1)
        }
        catch (ChannelClosedException)
        {
        }
    }

    public void RawWrite(string line)
    {
        lock (_lock)
        {
            if (_closed || _writer is null)
            {
                return;
            }

            var size = Encoding.UTF8.GetByteCount(line) + 2;
            if (_bytesInPart + size > _maxFileBytes && _bytesInPart > 0)
            {
                RotateLocked();
            }

            _writer.WriteLine(line);
            _bytesInPart += size;
            FlushLocked();
        }
    }

    public void CompleteAndJoin(TimeSpan timeout)
    {
        _channel.Writer.TryComplete();
        _thread.Join(timeout);
    }

    public void CrashShutdown(string finalLine)
    {
        lock (_lock)
        {
            if (!_closed && _writer is not null)
            {
                _writer.WriteLine(finalLine);
                _writer.Flush();
                CloseLocked();
                _closed = true;
            }
        }

        _channel.Writer.TryComplete();

        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromMilliseconds(1500));
        }
    }

    private void ThreadBody() => LoopAsync().GetAwaiter().GetResult(); // VERIFY(G1)

    private async Task LoopAsync()
    {
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                WriteEntry(entry);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_lock)
            {
                if (!_closed)
                {
                    CloseLocked();
                    _closed = true;
                }
            }
        }
    }

    private void WriteEntry(LogEntry entry)
    {
        lock (_lock)
        {
            if (_closed || _writer is null)
            {
                return;
            }

            var size = Encoding.UTF8.GetByteCount(entry.Text) + 2;
            if (_bytesInPart + size > _maxFileBytes && _bytesInPart > 0)
            {
                RotateLocked();
            }

            _writer.WriteLine(entry.Text);
            _bytesInPart += size;

            if (entry.Level >= LogLevel.Warning || Stopwatch.GetElapsedTime(_lastFlushTimestamp) >= FlushInterval)
            {
                FlushLocked();
            }
        }
    }

    private void FlushLocked()
    {
        _writer!.Flush();
        _lastFlushTimestamp = Stopwatch.GetTimestamp();
    }

    private void RotateLocked()
    {
        CloseLocked();
        _partIndex++;
        OpenPartLocked();
    }

    private void OpenPartLocked()
    {
        var name = _partIndex == 1 ? _baseName + ".txt" : $"{_baseName}.part{_partIndex}.txt";
        var path = Path.Combine(_directory, name);
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 65536);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false))
        {
            NewLine = "\r\n",
        };
        _bytesInPart = 0;
    }

    private void CloseLocked()
    {
        if (_writer is null)
        {
            return;
        }

        try
        {
            _writer.Flush();
        }
        finally
        {
            _writer.Dispose();
            _writer = null;
            _stream = null;
        }
    }

    private readonly record struct LogEntry(LogLevel Level, string Text);
}
