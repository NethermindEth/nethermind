using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

namespace Evm.JsonTypes
{
    public class EnvInfo
    {
        public Address? CurrentCoinbase { get; set; }
        public long CurrentGasLimit { get; set; }
        public ulong CurrentTimestamp { get; set; }
        public long CurrentNumber { get; set; }

        public string[] Withdrawals { get; set; } = [];

        public ulong ParentTimestamp { get; set; }
        public UInt256? ParentDifficulty { get; set; }
        public UInt256 CurrentBaseFee { get; set; }
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

        public BlockHeader GetBlockHeader()
        {
            BlockHeaderBuilder blockHeaderBuilder = new();
            if (CurrentDifficulty.HasValue)
            {
                blockHeaderBuilder.WithDifficulty(CurrentDifficulty.Value);
            }
            blockHeaderBuilder.WithNumber(CurrentNumber);
            blockHeaderBuilder.WithGasLimit(CurrentGasLimit);
            blockHeaderBuilder.WithBeneficiary(CurrentCoinbase ?? throw new Exception("CurrentCoinbase is missing"));
            blockHeaderBuilder.WithExcessBlobGas(CurrentExcessBlobGas);
            blockHeaderBuilder.WithParentBeaconBlockRoot(ParentBeaconBlockRoot);
            blockHeaderBuilder.WithBaseFee(CurrentBaseFee);
            blockHeaderBuilder.WithTimestamp(CurrentTimestamp);

            return blockHeaderBuilder.TestObject;
        }

        public BlockHeader GetParentBlockHeader()
        {
            BlockHeaderBuilder blockHeaderBuilder = new();
            if (ParentDifficulty.HasValue)
            {
                blockHeaderBuilder.WithDifficulty(ParentDifficulty.Value);
            }
            blockHeaderBuilder.WithNumber(CurrentNumber - 1);
            blockHeaderBuilder.WithGasLimit(ParentGasLimit);
            blockHeaderBuilder.WithExcessBlobGas(ParentExcessBlobGas);
            if (ParentBaseFee.HasValue)
            {
                blockHeaderBuilder.WithBaseFee(ParentBaseFee.Value);
            }
            blockHeaderBuilder.WithBlobGasUsed(ParentBlobGasUsed);
            blockHeaderBuilder.WithGasUsed(ParentGasUsed);
            blockHeaderBuilder.WithTimestamp(ParentTimestamp);
            if (ParentUncleHash != null)
            {
                blockHeaderBuilder.WithUnclesHash(ParentUncleHash);
            }
            return blockHeaderBuilder.TestObject;
        }
    }
}
