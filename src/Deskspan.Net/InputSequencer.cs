namespace Deskspan.Net;

public sealed class InputSequencer
{
    private const int MaxWaiting = 512;
    private readonly object _gate = new();
    private readonly SortedDictionary<uint, NetMessage> _waiting = new();
    private readonly Action<NetMessage> _deliver;
    private uint _next = 1;

    public InputSequencer(Action<NetMessage> deliver) => _deliver = deliver;

    public void Reset()
    {
        lock (_gate)
        {
            _waiting.Clear();
            _next = 1;
        }
    }

    public void Offer(uint id, NetMessage message)
    {
        lock (_gate)
        {
            if (id < _next || _waiting.ContainsKey(id))
                return;
            _waiting[id] = message;
            if (_waiting.Count > MaxWaiting)
                _next = _waiting.Keys.First();
            while (_waiting.Remove(_next, out var ready))
            {
                _next++;
                try
                {
                    _deliver(ready);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
