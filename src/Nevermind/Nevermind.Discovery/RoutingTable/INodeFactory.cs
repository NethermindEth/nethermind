namespace Nevermind.Discovery.RoutingTable
{
    public interface INodeFactory
    {
        Node CreateNode(byte[] id, string host, int port);
        Node CreateNode(string host, int port);
    }
}