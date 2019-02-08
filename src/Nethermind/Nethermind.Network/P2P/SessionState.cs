namespace Nethermind.Network.P2P
{
    public enum SessionState
    {
        New = 0,
        HandshakeComplete = 1,
        Initialized = 2,
        DisconnectingProtocols = 3,
        Disconnecting = 4,
        Disconnected = 5
    }
}