using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal.History;

public class ContentKey : IUnion
{
    [Selector(0)]
    public ValueHash256? HeaderKey { get; set; }

    [Selector(1)]
    public ValueHash256? BodyKey { get; set; }

    [Selector(2)]
    public ValueHash256? ReceiptKey { get; set; }

    [Selector(3)]
    public ulong? HeaderKeyByBlockNumber { get; set; }
}
