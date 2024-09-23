using Nethermind.Serialization.Ssz;

namespace Nethermind.Network.Discovery.Portal.Messages;

[SszSerializable]
public class MessageUnion
{
    public MessageType Selector { get; set; }

    public Ping? Ping { get; set; }

    public Pong? Pong { get; set; }

    public FindNodes? FindNodes { get; set; }

    public Nodes? Nodes { get; set; }

    public FindContent? FindContent { get; set; }

    public Content? Content { get; set; }

    public Offer? Offer { get; set; }

    public Accept? Accept { get; set; }
}

public enum MessageType
{
    Ping = 0,
    Pong = 1,
    FindNodes = 2,
    Nodes = 3,
    FindContent = 4,
    Content = 5,
    Offer = 6,
    Accept = 7,
}
