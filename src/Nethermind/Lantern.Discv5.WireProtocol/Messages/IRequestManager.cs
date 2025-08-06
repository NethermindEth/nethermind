namespace Lantern.Discv5.WireProtocol.Messages;

public interface IRequestManager
{
    int CachedRequestsCount { get; }

    int PendingRequestsCount { get; }

    int CachedHandshakeInteractionsCount { get; }

    void InitAsync();

    Task StopRequestManagerAsync();

    bool AddPendingRequest(byte[] requestId, PendingRequest request);

    bool AddCachedRequest(byte[] requestId, CachedRequest request);

    void AddCachedHandshakeInteraction(byte[] packetNonce, byte[] destNodeId);

    byte[]? GetCachedHandshakeInteraction(byte[] packetNonce);

    bool ContainsCachedRequest(byte[] requestId);

    PendingRequest? GetPendingRequest(byte[] requestId);

    PendingRequest? GetPendingRequestByNodeId(byte[] nodeId);

    CachedRequest? GetCachedRequest(byte[] requestId);

    PendingRequest? MarkRequestAsFulfilled(byte[] requestId);

    CachedRequest? MarkCachedRequestAsFulfilled(byte[] requestId);
}