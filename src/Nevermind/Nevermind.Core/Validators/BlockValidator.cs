using System;
using System.Numerics;

namespace Nevermind.Core.Validators
{
    public class BlockValidator
    {
        public bool Finalize(Block block)
        {
            // parallelize?
            foreach (BlockHeader ommerHeader in block.Ommers)
                if (IsValid(ommerHeader))
                    return false;

            // parallelize?
            foreach (Transaction transaction in block.Transactions)
                if (IsValid(transaction))
                    return false;

            return IsValid(block.Header);
        }

        public bool IsValid(Block block)
        {
            Block parent = Blockchain.GetParent(block);
            return
                //block.Header.Nonce < BigInteger.Divide(BigInteger.Pow(2, 256), block.Header.DifficultyCalculator) &&
                // mix hash check
                // proof of work check
                // difficulty check
                block.Header.GasUsed < block.Header.GasLimit &&
                block.Header.GasLimit < parent.Header.GasLimit + BigInteger.Divide(parent.Header.GasLimit, 1024) &&
                block.Header.GasLimit > parent.Header.GasLimit - BigInteger.Divide(parent.Header.GasLimit, 1024) &&
                block.Header.GasLimit > 125000 &&
                block.Header.Timestamp > parent.Header.Timestamp &&
                block.Header.Number == parent.Header.Number + 1 &&
                block.Header.ExtraData.Length <= 32;
        }

        public bool IsValid(BlockHeader block)
        {
            throw new NotImplementedException();
        }

        public bool IsValid(Transaction transaction)
        {
            throw new NotImplementedException();
        }
    }
}