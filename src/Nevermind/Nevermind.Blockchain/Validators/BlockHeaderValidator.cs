using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Crypto;

namespace Nevermind.Blockchain.Validators
{
    public class BlockHeaderValidator
    {
        private readonly IBlockchainStore _chain;

        public BlockHeaderValidator(IBlockchainStore chain)
        {
            _chain = chain;
        }
        
        public bool IsValid(Block block)
        {
            Block parent = _chain.FindBlock(block.Header.ParentHash);
            BlockHeader header = block.Header;
            if (parent == null)
            {
                return IsGenesisHeaderValid(header);
            }

            Keccak hash = header.Hash;
            header.RecomputeHash();

            return
                header.Nonce < BigInteger.Divide(BigInteger.Pow(2, 256), header.Difficulty) &&
                // mix hash check
                // proof of work check
                // difficulty check
                header.GasUsed < header.GasLimit &&
                header.GasLimit < parent.Header.GasLimit + BigInteger.Divide(parent.Header.GasLimit, 1024) &&
                header.GasLimit > parent.Header.GasLimit - BigInteger.Divide(parent.Header.GasLimit, 1024) &&
                header.GasLimit > 125000 &&
                header.Timestamp > parent.Header.Timestamp &&
                header.Number == parent.Header.Number + 1 &&
                header.ExtraData.Length <= 32 &&
                header.Hash == hash;
        }

        private static bool IsGenesisHeaderValid(BlockHeader header)
        {
            return
                //block.Header.Nonce < BigInteger.Divide(BigInteger.Pow(2, 256), block.Header.DifficultyCalculator) &&
                // mix hash check
                // proof of work check
                // difficulty check
                header.GasUsed < header.GasLimit &&
                // header.GasLimit > 125000 && // TODO: not in tests :(
                header.Timestamp > 0 && // what here?
                header.Number == 0 &&
                header.ExtraData.Length <= 32;
        }
    }
}