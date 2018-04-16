namespace Nethermind.Network.P2P
{
    public interface IDiscoveryListener
    {
        void OnNodeDiscovered(DiscoveryNode node);
    }
}