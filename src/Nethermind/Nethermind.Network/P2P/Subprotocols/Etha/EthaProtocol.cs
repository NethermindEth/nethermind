using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;

namespace Nethermind.Network.P2P.Subprotocols.Etha
{
    public class EthaProtocol : Protocol
    {
        public override string Name => "etha";
        public override byte Version => 1;
        public override int MessageIdSpaceSize => 3; // Number of messages in protocol

        public EthaProtocol() : base(nameof(EthaProtocol))
        {
            // Protocol initialization
        }
    }
} 
