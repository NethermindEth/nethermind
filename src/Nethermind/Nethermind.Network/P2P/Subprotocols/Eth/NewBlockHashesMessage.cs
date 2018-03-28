using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class NewBlockHashesMessage : P2PMessage
    {
        public override int PacketType { get; } = 1;
        public override int Protocol { get; } = 1;

        public List<(Keccak, BigInteger)> BlockHashes { get; set; }

        public NewBlockHashesMessage()
        {
        }

        public NewBlockHashesMessage(params (Keccak, BigInteger)[] blockHashes)
        {
            BlockHashes = blockHashes.ToList();
        }
    }
}