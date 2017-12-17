using Nevermind.Core.Crypto;

namespace Nevermind.Core.Test
{
    public class BlockHeaderBuilder : TestObjectBuilder<BlockHeader>
    {
        public override BlockHeader ForTest()
        {
            BlockHeader blockHeader = new BlockHeader(Keccak.Compute("parent"), Keccak.OfAnEmptySequenceRlp, Address.Zero, 1_000_000, 1, 4_000_000, 1_000_000, new byte[] {1, 2, 3});
            blockHeader.Bloom = new Bloom();
            blockHeader.MixHash = Keccak.Compute("mix_hash");
            blockHeader.Nonce = 1000;
            blockHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
            blockHeader.StateRoot = Keccak.EmptyTreeHash;
            blockHeader.TransactionsRoot = Keccak.EmptyTreeHash;
            blockHeader.RecomputeHash();
            return blockHeader;
        }
    }
}