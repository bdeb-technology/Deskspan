namespace Deskspan.Net;

public sealed class InputSequencer
{
    private const int MaxWaiting = 512;
    private static readonly long GapWait = (long)TimeSpan.FromMilliseconds(80).TotalMilliseconds;
    private readonly object _gate = new();
    private readonly SortedDictionary<uint, NetMessage> _waiting = new();
    private readonly Action<NetMessage> _deliver;
    private readonly Func<long> _now;
    private uint _next = 1;
    private long _gapSince;

    public InputSequencer(Action<NetMessage> deliver, Func<long>? now = null)
    {
        _deliver = deliver;
        _now = now ?? (() => Environment.TickCount64);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _waiting.Clear();
            _next = 1;
            _gapSince = 0;
        }
    }

    public void Offer(uint id, NetMessage message)
    {
        // Deliver inside the lock so that concurrent offers from the UDP and TCP
        // paths cannot interleave and hand the receiver input out of order.
        lock (_gate)
        {
            if (id >= _next && !_waiting.ContainsKey(id))
                _waiting[id] = message;
            while (_waiting.Count > MaxWaiting)
                _waiting.Remove(_waiting.Keys.Last());
            if (_waiting.Count > 0 && !_waiting.ContainsKey(_next))
            {
                var now = _now();
                if (_gapSince == 0)
                    _gapSince = now;
                else if (now - _gapSince >= GapWait)
                {
                    _next = _waiting.Keys.First();
                    _gapSince = 0;
                }
            }
            else
            {
                _gapSince = 0;
            }

            while (_waiting.Remove(_next, out var next))
            {
                _next++;
                try
                {
                    _deliver(next);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
