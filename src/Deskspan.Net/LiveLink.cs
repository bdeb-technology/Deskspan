using System.Threading.Channels;

namespace Deskspan.Net;

public sealed class LiveLink : IAsyncDisposable
{
    private readonly SecureStream _stream;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _receive;
    private readonly Task _heartbeat;
    private readonly Task _writer;
    private readonly Channel<NetMessage> _reliable = Channel.CreateUnbounded<NetMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _moveGate = new();
    private readonly TaskCompletionSource<NetMessage.Hello> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NetMessage.PointerMove? _pendingMove;
    private long _lastReceive;
    private int _closed;

    private LiveLink(SecureStream stream, TimeSpan receiveTimeout)
    {
        _stream = stream;
        _lastReceive = Environment.TickCount64;
        _writer = Task.Run(() => WriteLoopAsync(_cancel.Token));
        _receive = Task.Run(() => ReceiveLoopAsync(_cancel.Token));
        _heartbeat = Task.Run(() => HeartbeatLoopAsync(receiveTimeout, _cancel.Token));
    }

    public event Action<NetMessage>? MessageReceived;
    public event Action? Closed;
    public NetMessage.Hello? RemoteHello { get; private set; }

    public static async Task<LiveLink> StartAsync(Stream stream, byte[] tcpKey, NetMessage.Hello localHello, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
    {
        var secure = new SecureStream(stream, tcpKey);
        var link = new LiveLink(secure, receiveTimeout ?? Protocol.ReceiveTimeout);
        try
        {
            await link.SendAsync(localHello, cancellationToken).ConfigureAwait(false);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(TimeSpan.FromSeconds(8));
            link.RemoteHello = await link._hello.Task.WaitAsync(wait.Token).ConfigureAwait(false);
            return link;
        }
        catch
        {
            await link.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task SendAsync(NetMessage message, CancellationToken cancellationToken)
    {
        if (message is NetMessage.PointerMove move)
        {
            lock (_moveGate)
            {
                if (_pendingMove == null || Sequence.IsNewer(move.Sequence, _pendingMove.Sequence))
                    _pendingMove = move;
            }
        }
        else
        {
            _reliable.Writer.TryWrite(message);
        }

        Wake();
        return Task.CompletedTask;
    }

    private void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

                while (_reliable.Reader.TryRead(out var reliable))
                    await _stream.SendAsync(reliable, cancellationToken).ConfigureAwait(false);

                NetMessage.PointerMove? move;
                lock (_moveGate)
                {
                    move = _pendingMove;
                    _pendingMove = null;
                }

                if (move != null)
                    await _stream.SendAsync(move, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            SignalClosed();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await _stream.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastReceive, Environment.TickCount64);
                if (message is NetMessage.Hello hello)
                {
                    RemoteHello = hello;
                    _hello.TrySetResult(hello);
                    continue;
                }

                if (message is NetMessage.Heartbeat)
                    continue;

                MessageReceived?.Invoke(message);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            SignalClosed();
        }
    }

    private async Task HeartbeatLoopAsync(TimeSpan receiveTimeout, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(Protocol.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                if (Environment.TickCount64 - Interlocked.Read(ref _lastReceive) > receiveTimeout.TotalMilliseconds)
                {
                    SignalClosed();
                    break;
                }

                await SendAsync(new NetMessage.Heartbeat(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            SignalClosed();
        }
    }

    private void SignalClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            Closed?.Invoke();
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cancel.Cancel();
        _reliable.Writer.TryComplete();
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await _writer.ConfigureAwait(false); } catch { }
        try { await _receive.ConfigureAwait(false); } catch { }
        try { await _heartbeat.ConfigureAwait(false); } catch { }
        _cancel.Dispose();
        _signal.Dispose();
    }
}
