using System.Numerics;

namespace Nevermind.Core.Validators
{
    public class BlockHeaderValidator
    {
        public static bool IsValid(BlockHeader header)
        {
            Block parent = Blockchain.GetParent(header);
            if (parent == null)
            {
                return IsGenesisHeaderValid(header);
            }

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
                header.ExtraData.Length <= 32;
        }

        private static bool IsGenesisHeaderValid(BlockHeader header)
        {
            return
                //block.Header.Nonce < BigInteger.Divide(BigInteger.Pow(2, 256), block.Header.DifficultyCalculator) &&
                // mix hash check
                // proof of work check
                // difficulty check
                header.GasUsed < header.GasLimit &&
                header.GasLimit > 125000 &&
                header.Timestamp > 0 && // what here?
                header.Number == 0 &&
                header.ExtraData.Length <= 32;
        }
    }
}