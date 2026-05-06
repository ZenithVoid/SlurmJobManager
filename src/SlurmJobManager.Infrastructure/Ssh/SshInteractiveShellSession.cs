using System.Text;
using Renci.SshNet;
using SlurmJobManager.Core.Interfaces;

namespace SlurmJobManager.Infrastructure.Ssh;

internal sealed class SshInteractiveShellSession : IInteractiveShellSession
{
    private readonly ShellStream _shellStream;
    private readonly CancellationTokenSource _readerCts = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Task _readerTask;
    private int _closed;

    public event EventHandler<string>? OutputReceived;
    public event EventHandler? Closed;

    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    public SshInteractiveShellSession(ShellStream shellStream)
    {
        _shellStream = shellStream ?? throw new ArgumentNullException(nameof(shellStream));
        _readerTask = Task.Run(() => ReaderLoopAsync(_readerCts.Token));
    }

    public async Task WriteAsync(string data, CancellationToken ct = default)
    {
        if (!IsOpen) throw new InvalidOperationException("Interactive shell session is closed.");
        if (string.IsNullOrEmpty(data)) return;

        await _writeGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                _shellStream.Write(data);
                _shellStream.Flush();
            }, ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task ResizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        if (!IsOpen) return Task.CompletedTask;
        cols = Math.Max(2, cols);
        rows = Math.Max(2, rows);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            _shellStream.SendWindowChangeRequest((uint)cols, (uint)rows, 0, 0);
        }, ct);
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        _readerCts.Cancel();
        try { await _readerTask; } catch (OperationCanceledException) { /* normal */ }
        catch { /* best effort */ }

        try { _shellStream.Dispose(); } catch { /* best effort */ }
        try { _writeGate.Dispose(); } catch { /* best effort */ }
        try { _readerCts.Dispose(); } catch { /* best effort */ }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReaderLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var utf8 = new UTF8Encoding(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_shellStream.DataAvailable)
                {
                    await Task.Delay(15, ct);
                    continue;
                }

                var read = _shellStream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    await Task.Delay(15, ct);
                    continue;
                }

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
            catch
            {
                await Task.Delay(30, ct);
            }
        }
    }

    public void Dispose()
    {
        try { CloseAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
    }
}
