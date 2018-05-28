using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth;

namespace Nethermind.Network.P2P
{
    public class Eth62ProtocolInitializedEventArgs : ProtocolInitializedEventArgs
    {
        public string Protocol { get; set; }
        public byte ProtocolVersion { get; set; }
        public long ChainId { get; set; }
        public BigInteger TotalDifficulty { get; set; }
        public Keccak BestHash { get; set; }
        public Keccak GenesisHash { get; set; }

        public Eth62ProtocolInitializedEventArgs(Eth62ProtocolHandler protocolHandler) : base(protocolHandler)
        {
        }
    }
}