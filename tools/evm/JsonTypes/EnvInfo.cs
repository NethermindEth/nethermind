using Microsoft.ClearScript.Util.Web;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Evm.JsonTypes
{
    public class EnvInfo
    {
        public Address? CurrentCoinbase { get; set; }
        public long CurrentGasLimit { get; set; }
        public ulong CurrentTimestamp { get; set; }
        public long CurrentNumber { get; set; }

        public string[]? Withdrawals { get; set; }

        public ulong ParentTimestamp { get; set; }
        public UInt256? ParentDifficulty { get; set; }
        public UInt256? CurrentBaseFee { get; set; }
        public UInt256? CurrentDifficulty { get; set; }
        public Hash256? ParentUncleHash { get; set; }
        public Hash256? ParentBeaconBlockRoot { get; set; }
        public UInt256? ParentBaseFee { get; set; }
        public long ParentGasUsed { get; set; }
        public long ParentGasLimit { get; set; }
        public ulong? ParentExcessBlobGas { get; set; }
        public ulong? CurrentExcessBlobGas { get; set; }
        public ulong? ParentBlobGasUsed { get; set; }
        public Dictionary<string, Hash256> BlockHashes { get; set; } = [];

        public BlockHeader header;
        public BlockHeader parent;

        public EnvInfo()
        {
            header = GetBlockHeader();
            parent = GetParentBlockHeader();
        }


        public BlockHeader GetBlockHeader()
        {
            BlockHeaderBuilder blockHeaderBuilder = new();
            if (CurrentDifficulty.HasValue) blockHeaderBuilder.WithDifficulty(CurrentDifficulty.Value);
            blockHeaderBuilder.WithNumber(CurrentNumber);
            blockHeaderBuilder.WithGasLimit(CurrentGasLimit);
            blockHeaderBuilder.WithBeneficiary(CurrentCoinbase ?? throw new Exception("CurrentCoinbase is missing"));
            blockHeaderBuilder.WithExcessBlobGas(CurrentExcessBlobGas);
            blockHeaderBuilder.WithParentBeaconBlockRoot(ParentBeaconBlockRoot);
            if (CurrentBaseFee.HasValue) blockHeaderBuilder.WithBaseFee(CurrentBaseFee.Value);
            blockHeaderBuilder.WithTimestamp(CurrentTimestamp);

            return blockHeaderBuilder.TestObject;
        }

        public BlockHeader GetParentBlockHeader()
        {
            BlockHeaderBuilder blockHeaderBuilder = new();
            if (ParentDifficulty.HasValue) blockHeaderBuilder.WithDifficulty(ParentDifficulty.Value);
            blockHeaderBuilder.WithNumber(CurrentNumber - 1);
            blockHeaderBuilder.WithGasLimit(ParentGasLimit);
            blockHeaderBuilder.WithExcessBlobGas(ParentExcessBlobGas);
            if (ParentBaseFee.HasValue) blockHeaderBuilder.WithBaseFee(ParentBaseFee.Value);
            blockHeaderBuilder.WithBlobGasUsed(ParentBlobGasUsed);
            blockHeaderBuilder.WithGasUsed(ParentGasUsed);
            blockHeaderBuilder.WithTimestamp(ParentTimestamp);
            if (ParentUncleHash != null) blockHeaderBuilder.WithUnclesHash(ParentUncleHash);
            return blockHeaderBuilder.TestObject;
        }

        public void ApplyChecks(ISpecProvider specProvider, IReleaseSpec spec)
        {
            ApplyLondonChecks(spec);
            ApplyShanghaiChecks(spec);
            ApplyCancunChecks(spec);
            ApplyMergeChecks(specProvider);
        }

        private void ApplyLondonChecks(IReleaseSpec spec)
        {
            if (spec is not London) return;
            if (CurrentBaseFee != null) return;

            if (ParentBaseFee.HasValue && CurrentNumber != 0)
            {
                CurrentBaseFee = BaseFeeCalculator.Calculate(ParentBaseFee.Value, ParentGasUsed, ParentGasLimit, CurrentNumber - 1, spec);
            }

            throw new Exception("EIP-1559 config but missing 'currentBaseFee' in env section");
        }

        private void ApplyShanghaiChecks(IReleaseSpec spec)
        {
            if (spec is not Shanghai) return;
            if (Withdrawals == null)
            {
                throw new Exception("Shanghai config but missing 'withdrawals' in env section");
            }
        }

        private void ApplyCancunChecks(IReleaseSpec spec)
        {
            if (spec is not Cancun)
            {
                ParentBeaconBlockRoot = null;
                return;
            }

            if (ParentBeaconBlockRoot == null)
            {
                throw new Exception("post-cancun env requires parentBeaconBlockRoot to be set");
            }
        }

        private void ApplyMergeChecks(ISpecProvider specProvider)
        {
            if (CurrentDifficulty != null) return;
            if (!ParentDifficulty.HasValue)
            {
                throw new Exception(
                    "currentDifficulty was not provided, and cannot be calculated due to missing parentDifficulty");
            }

            if (CurrentNumber == 0)
            {
                throw new Exception("currentDifficulty needs to be provided for block number 0");
            }

            if (CurrentTimestamp <= ParentTimestamp)
            {
                throw new Exception($"currentDifficulty cannot be calculated -- currentTime ({CurrentTimestamp}) needs to be after parent time ({ParentTimestamp})");
            }

            EthashDifficultyCalculator difficultyCalculator = new(specProvider);

            CurrentDifficulty = difficultyCalculator.Calculate(header, parent);
        }
    }
}
