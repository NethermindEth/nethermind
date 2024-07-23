using System;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Network.Discovery.Portal;

public class MessageUnion: IUnion
{
    [Selector(0)] public Ping? Ping { get; set; }

    [Selector(1)] public Pong? Pong { get; set; }

    [Selector(2)] public FindNodes? FindNodes { get; set; }

    [Selector(3)] public Nodes? Nodes { get; set; }

    [Selector(4)] public FindContent? FindContent { get; set; }

    [Selector(5)] public Content? Content { get; set; }
}
