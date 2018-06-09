using Nethermind.Network.P2P;

namespace Nethermind.Network.Discovery.Stats
{
    public class NodeDetails
    {
        public string ClientId { get; set; }
        public Capability[] Capabilities { get; set; }
    }
}