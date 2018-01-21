namespace Nevermind.Discovery.RoutingTable
{
    public interface INodeTable
    {
        NodeAddResult AddNode(Node node);
        void DeleteNode(Node node);
    }
}