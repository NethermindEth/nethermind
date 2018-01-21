using Nevermind.Core.Crypto;

namespace Nevermind.Discovery.RoutingTable
{
    public class Node
    {
        public Node(byte[] id)
        {
            Id = id;
            IdHash = Keccak.Compute(id).Bytes;
        }

        public byte[] Id { get; }
        public byte[] IdHash { get; }
        public string Host { get; set; }
        public int Port { get; set; }
        public bool IsDicoveryNode { get; set; }
    }
}