using System.Text;
using System.Reflection;
using System.Diagnostics;
using System.Threading.Channels;
using Renci.SshNet;
using Renci.SshNet.Common;
using SlurmJobManager.Core.Interfaces;

namespace SlurmJobManager.Infrastructure.Ssh;

internal sealed class SshInteractiveShellSession : IInteractiveShellSession
{
    // Reflection-based PTY resize is currently validated with SSH.NET 2024.2.0.
    private static readonly TimeSpan ReaderShutdownTimeout = TimeSpan.FromSeconds(2);
    private const int ReadPollTimeoutMs = 50;
    private readonly ShellStream _shellStream;
    private readonly CancellationTokenSource _readerCts = new();
    private readonly Channel<string> _writeQueue;
    private readonly Task _readerTask;
    private readonly Task _writerTask;
    private int _closed;

    public event EventHandler<string>? OutputReceived;
    public event EventHandler? Closed;

    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    public SshInteractiveShellSession(ShellStream shellStream)
    {
        _shellStream = shellStream ?? throw new ArgumentNullException(nameof(shellStream));
        _writeQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        try { _shellStream.ReadTimeout = ReadPollTimeoutMs; } catch { /* best effort */ }
        _readerTask = Task.Run(() => ReaderLoop(_readerCts.Token));
        _writerTask = Task.Run(() => WriterLoop(_readerCts.Token));
    }

    public async Task WriteAsync(string data, CancellationToken ct = default)
    {
        if (!IsOpen) throw new InvalidOperationException("Interactive shell session is closed.");
        if (string.IsNullOrEmpty(data)) return;
        if (TryWrite(data)) return;
        if (!IsOpen) throw new InvalidOperationException("Interactive shell session is closed.");

        while (await _writeQueue.Writer.WaitToWriteAsync(ct))
        {
            if (_writeQueue.Writer.TryWrite(data))
                return;
        }

        throw new InvalidOperationException("Interactive shell session is closed.");
    }

    public bool TryWrite(string data)
    {
        if (!IsOpen || string.IsNullOrEmpty(data))
            return false;
        return _writeQueue.Writer.TryWrite(data);
    }

    public Task ResizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        if (!IsOpen) return Task.CompletedTask;

        cols = Math.Max(2, cols);
        rows = Math.Max(2, rows);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var channelField = _shellStream.GetType().GetField("_channel", BindingFlags.Instance | BindingFlags.NonPublic);
                var channel = channelField?.GetValue(_shellStream);
                if (channel == null)
                {
                    Debug.WriteLine("[SshInteractiveShellSession] Resize skipped: ShellStream channel field is unavailable.");
                    return;
                }

                var method = channel.GetType().GetMethod(
                    "SendWindowChangeRequest",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(uint), typeof(uint), typeof(uint), typeof(uint) },
                    modifiers: null);

                if (method == null)
                {
                    Debug.WriteLine("[SshInteractiveShellSession] Resize skipped: SendWindowChangeRequest is unavailable.");
                    return;
                }

                // Pixel width/height are intentionally set to 0 because most SSH servers
                // rely on character cell dimensions for PTY resize handling.
                _ = method.Invoke(channel, new object[] { (uint)cols, (uint)rows, 0u, 0u });
            }
            catch (TargetInvocationException ex)
            {
                Debug.WriteLine($"[SshInteractiveShellSession] Resize invocation failed: {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (MethodAccessException ex)
            {
                Debug.WriteLine($"[SshInteractiveShellSession] Resize access denied: {ex.Message}");
            }
            catch (ArgumentException ex)
            {
                Debug.WriteLine($"[SshInteractiveShellSession] Resize argument mismatch: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"[SshInteractiveShellSession] Resize unsupported: {ex.Message}");
            }
        }, ct);
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        _readerCts.Cancel();
        _writeQueue.Writer.TryComplete();

        try { _shellStream.Dispose(); } catch { /* best effort */ }

        try
        {
            using var timeoutCts = new CancellationTokenSource(ReaderShutdownTimeout);
            var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);
            var completed = await Task.WhenAny(_readerTask, timeoutTask);
            if (completed == _readerTask)
            {
                timeoutCts.Cancel();
                await _readerTask;
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch { /* best effort */ }

        try
        {
            using var timeoutCts = new CancellationTokenSource(ReaderShutdownTimeout);
            var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);
            var completed = await Task.WhenAny(_writerTask, timeoutTask);
            if (completed == _writerTask)
            {
                timeoutCts.Cancel();
                await _writerTask;
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch { /* best effort */ }

        try { _readerCts.Dispose(); } catch { /* best effort */ }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private Task ReaderLoop(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var utf8 = new UTF8Encoding(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var read = _shellStream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;

                var text = utf8.GetString(buffer, 0, read);
                if (text.Length > 0)
                    OutputReceived?.Invoke(this, text);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SshOperationTimeoutException)
            {
                continue;
            }
            catch
            {
                break;
            }
        }

        return Task.CompletedTask;
    }

    private async Task WriterLoop(CancellationToken ct)
    {
        var reader = _writeQueue.Reader;
        while (!ct.IsCancellationRequested)
        {
            string? chunk;
            try
            {
                if (!await reader.WaitToReadAsync(ct))
                    break;
                if (!reader.TryRead(out chunk))
                    continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                if (string.IsNullOrEmpty(chunk))
                    continue;
                _shellStream.Write(chunk);

                while (reader.TryRead(out var bufferedChunk))
                {
                    if (string.IsNullOrEmpty(bufferedChunk))
                        continue;
                    _shellStream.Write(bufferedChunk);
                }

                _shellStream.Flush();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        try { CloseAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
    }

}
