using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Portal.Messages;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Network.Discovery.Portal.History;

[SszSerializable]
public class HistoryContentKey : IUnion
{
    public HistoryContentType Selector { get; set; }

    [SszVector(32)]
    public byte[] HeaderByHash { get; set; } = Array.Empty<byte>();

    [SszVector(32)]
    public byte[] BodyByHash { get; set; } = Array.Empty<byte>();

    [SszVector(32)]
    public byte[] ReceiptByHash { get; set; } = Array.Empty<byte>();

    public ulong HeaderByBlockNumber { get; set; }
}

public enum HistoryContentType
{
    HeaderByHash = 0,
    BodyByHash = 1,
    ReceiptByHash = 2,
    HeaderByBlockNumber = 3,
}
