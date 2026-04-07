namespace NzbWebDAV.Websocket;

public class WebsocketTopic
{
    private static Dictionary<string, WebsocketTopic>? _topicsByName;

    // Stateful topics
    public static readonly WebsocketTopic UsenetConnections = new("cxs", TopicType.State);
    public static readonly WebsocketTopic SymlinkTaskProgress = new("stp", TopicType.State);
    public static readonly WebsocketTopic CleanupTaskProgress = new("ctp", TopicType.State);
    public static readonly WebsocketTopic StrmToSymlinksTaskProgress = new("st2sy", TopicType.State);
    public static readonly WebsocketTopic QueueItemStatus = new("qs", TopicType.State);
    public static readonly WebsocketTopic QueueItemProgress = new("qp", TopicType.State);
    public static readonly WebsocketTopic HealthItemStatus = new("hs", TopicType.State);
    public static readonly WebsocketTopic HealthItemProgress = new("hp", TopicType.State);

    // Eventful topics
    public static readonly WebsocketTopic QueueItemAdded = new("qa", TopicType.Event);
    public static readonly WebsocketTopic QueueItemRemoved = new("qr", TopicType.Event);
    public static readonly WebsocketTopic HistoryItemAdded = new("ha", TopicType.Event);
    public static readonly WebsocketTopic HistoryItemRemoved = new("hr", TopicType.Event);

    public readonly string Name;
    public readonly TopicType Type;

    private WebsocketTopic(string name, TopicType type)
    {
        Name = name;
        Type = type;
    }

    public static bool TryFromName(string name, out WebsocketTopic? topic)
    {
        var found = TopicsByName.TryGetValue(name, out var matchedTopic);
        topic = matchedTopic;
        return found;
    }

    private static Dictionary<string, WebsocketTopic> TopicsByName => _topicsByName ??= CreateTopicsByName();

    private static Dictionary<string, WebsocketTopic> CreateTopicsByName()
    {
        return new Dictionary<string, WebsocketTopic>(StringComparer.Ordinal)
        {
            [UsenetConnections.Name] = UsenetConnections,
            [SymlinkTaskProgress.Name] = SymlinkTaskProgress,
            [CleanupTaskProgress.Name] = CleanupTaskProgress,
            [StrmToSymlinksTaskProgress.Name] = StrmToSymlinksTaskProgress,
            [QueueItemStatus.Name] = QueueItemStatus,
            [QueueItemProgress.Name] = QueueItemProgress,
            [HealthItemStatus.Name] = HealthItemStatus,
            [HealthItemProgress.Name] = HealthItemProgress,
            [QueueItemAdded.Name] = QueueItemAdded,
            [QueueItemRemoved.Name] = QueueItemRemoved,
            [HistoryItemAdded.Name] = HistoryItemAdded,
            [HistoryItemRemoved.Name] = HistoryItemRemoved,
        };
    }

    public enum TopicType
    {
        State,
        Event
    }
}
