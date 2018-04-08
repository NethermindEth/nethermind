namespace Nethermind.Network.P2P
{
    public interface IDiscoveryListener
    {
        void OnNewNodeDiscovered(DiscoveryNode node);
    }
}