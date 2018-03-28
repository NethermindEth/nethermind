using System.Linq;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class NewBlockHasheshMessageSerializer : IMessageSerializer<NewBlockHashesMessage>
    {
        public byte[] Serialize(NewBlockHashesMessage message, IMessagePad pad = null)
        {
            return Rlp.Encode(
                message.BlockHashes.Select(bh =>
                    Rlp.Encode(
                        Rlp.Encode(bh.Item1),
                        Rlp.Encode(bh.Item2))).ToArray()
            ).Bytes;
        }

        public NewBlockHashesMessage Deserialize(byte[] bytes)
        {
            DecodedRlp decodedRlp = Rlp.Decode(new Rlp(bytes));
            (Keccak, BigInteger)[] blockHashes = new (Keccak, BigInteger)[decodedRlp.Length];
            for (int i = 0; i < decodedRlp.Length; i++)
            {
                DecodedRlp blockHashRlp = decodedRlp.GetSequence(i);
                blockHashes[i] = (blockHashRlp.GetKeccak(0), blockHashRlp.GetUnsignedBigInteger(1));
            }

            return new NewBlockHashesMessage(blockHashes);
        }
    }
}