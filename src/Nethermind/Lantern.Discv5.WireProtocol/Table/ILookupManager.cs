using Lantern.Discv5.Enr;

namespace Lantern.Discv5.WireProtocol.Table;

public interface ILookupManager
{
    Task<List<IEnr>?> LookupAsync(byte[] targetNodeId);

    Task StartLookupAsync(byte[] targetNodeId);

    public Task ContinueLookupAsync(List<NodeTableEntry> nodes, byte[] senderNodeId, int expectedResponses);

    bool IsLookupInProgress { get; }
}