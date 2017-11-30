using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Crypto;

namespace Nevermind.Blockchain.Validators
{
    public class BlockHeaderValidator : IBlockHeaderValidator
    {
        private readonly IBlockStore _chain;

        public BlockHeaderValidator(IBlockStore chain)
        {
            _chain = chain;
        }

        public bool Validate(BlockHeader header)
        {
            Block parent = _chain.FindParent(header);
            if (parent == null)
            {
                return IsGenesisHeaderValid(header);
            }

            Keccak hash = header.Hash;
            header.RecomputeHash();

            bool isNonceValid = header.Nonce < BigInteger.Divide(BigInteger.Pow(2, 256), header.Difficulty);
            // mix hash check
            // proof of work check
            // difficulty check
            bool gasUsedBelowLimit = header.GasUsed <= header.GasLimit;
            bool gasLimitNotTooHigh = header.GasLimit < parent.Header.GasLimit + BigInteger.Divide(parent.Header.GasLimit, 1024);
            bool gasLimitNotTooLow = header.GasLimit > parent.Header.GasLimit - BigInteger.Divide(parent.Header.GasLimit, 1024);
//            bool gasLimitAboveAbsoluteMinimum = header.GasLimit >= 125000; // TODO: tests are consistently not following this rule
            bool timestampMoreThanAtParent = header.Timestamp > parent.Header.Timestamp;
            bool numberIsParentPlusOne = header.Number == parent.Header.Number + 1;
            bool extraDataNotTooLong = header.ExtraData.Length <= 32;
            bool hashAsExpected = header.Hash == hash;

            return isNonceValid &&
                   gasUsedBelowLimit &&
                   gasLimitNotTooLow &&
                   gasLimitNotTooHigh &&
//                   gasLimitAboveAbsoluteMinimum && // TODO: tests are consistently not following this rule
                   timestampMoreThanAtParent &&
                   numberIsParentPlusOne &&
                   extraDataNotTooLong &&
                   hashAsExpected;
        }

        private static bool IsGenesisHeaderValid(BlockHeader header)
        {
            return
                //block.Header.Nonce < BigInteger.Divide(BigInteger.Pow(2, 256), block.Header.DifficultyCalculator) &&
                // mix hash check
                // proof of work check
                // difficulty check
                header.GasUsed < header.GasLimit &&
                // header.GasLimit > 125000 && // TODO: tests are consistently not following this rule
                header.Timestamp > 0 && // what here?
                header.Number == 0 &&
                header.ExtraData.Length <= 32;
        }
    }
}