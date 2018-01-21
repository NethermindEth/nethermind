using Nevermind.Core;
using Nevermind.Core.Crypto;

namespace Nevermind.Discovery.RoutingTable
{
    public class NodeFactory : INodeFactory
    {
        public Node CreateNode(byte[] id, string host, int port)
        {
            return new Node(id)
            {
                Host = host,
                Port = port,
                IsDicoveryNode = false
            };
        }

        public Node CreateNode(string host, int port)
        {
            return new Node(new Hex(Keccak.Compute($"{host}:{port}").Bytes))
            {
                Host = host,
                Port = port,
                IsDicoveryNode = true
            };
        }
    }
}