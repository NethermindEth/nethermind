namespace Nethermind.Network.Discovery.RoutingTable
{
    public interface ITopic
    {
        string Name { get; set; }
        override string ToString();
    }
}