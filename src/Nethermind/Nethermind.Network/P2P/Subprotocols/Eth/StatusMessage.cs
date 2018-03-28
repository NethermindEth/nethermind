using System.Numerics;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class StatusMessage : P2PMessage
    {
        public override int PacketType { get; } = 0;
        public override int Protocol { get; } = 1;
        public int ProtocolVersion { get; set; } = 62;
        public int NetworkId { get; set; } = 1; // TODO: add support for network IDs, 1 is for mainnet here
        public BigInteger TotalDifficulty { get; set; }
        public Keccak BestHash { get; set; }
        public Keccak GenesisHash { get; set; }
    }
}