namespace Nethermind.Network.Discovery.RoutingTable
{
    public interface ITopic
    {
        string Name { get; set; }
        string ToString();
    }
}