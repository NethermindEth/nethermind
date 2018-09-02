namespace Nethermind.Network
{
    public enum ActivePeerSelectionCounter
    {
        AllNonActiveCandidates,
        FilteredByZeroPort,
        FilteredByDisconnect,
        FilteredByFailedConnection
    }
}