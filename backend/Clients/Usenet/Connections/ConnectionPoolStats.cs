using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Clients.Usenet.Connections;

public class ConnectionPoolStats
{
    private readonly int[] _live;
    private readonly int[] _idle;
    private readonly int[] _max;
    private int _totalLive;
    private int _totalIdle;
    private int _totalMax;
    private readonly UsenetProviderConfig _providerConfig;
    private readonly WebsocketManager _websocketManager;

    public ConnectionPoolStats(UsenetProviderConfig providerConfig, WebsocketManager websocketManager)
    {
        var count = providerConfig.Providers.Count;
        _live = new int[count];
        _idle = new int[count];
        _max = providerConfig.Providers
            .Select(x => x.Type == ProviderType.Pooled ? x.MaxConnections : 0)
            .ToArray();
        _totalMax = _max.Sum();

        _providerConfig = providerConfig;
        _websocketManager = websocketManager;
    }

    public EventHandler<ConnectionPoolChangedEventArgs> GetOnConnectionPoolChanged(int providerIndex)
    {
        return OnEvent;

        void OnEvent(object? _, ConnectionPoolChangedEventArgs args)
        {
            if (_providerConfig.Providers[providerIndex].Type == ProviderType.Pooled)
            {
                lock (this)
                {
                    _live[providerIndex] = args.Live;
                    _idle[providerIndex] = args.Idle;
                    _max[providerIndex] = args.Max;
                    _totalLive = _live.Sum();
                    _totalIdle = _idle.Sum();
                    _totalMax = _max.Sum();
                }
            }

            var message = $"{providerIndex}|{args.Live}|{args.Idle}|{_totalLive}|{_totalMax}|{_totalIdle}";
            _websocketManager.SendMessage(WebsocketTopic.UsenetConnections, message);
        }
    }

    public int TotalLive
    {
        get { lock (this) return _totalLive; }
    }

    public int TotalIdle
    {
        get { lock (this) return _totalIdle; }
    }

    public int MaxPooled
    {
        get { lock (this) return _totalMax; }
    }
    public int TotalActive => TotalLive - TotalIdle;

    public sealed class ConnectionPoolChangedEventArgs(int live, int idle, int max) : EventArgs
    {
        public int Live { get; } = live;
        public int Idle { get; } = idle;
        public int Max { get; } = max;
        public int Active => Live - Idle;
    }
}
