using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        public UInt256 ParentDifficulty { get; set; }
        public UInt256 CurrentBaseFee { get; set; }
        public UInt256 CurrentDifficulty { get; set; }
        public Hash256? ParentUncleHash { get; set; }
        public Hash256? ParentBeaconBlockRoot { get; set; }
        public Hash256? CurrentRandom { get; set; }
        public UInt256 ParentBaseFee { get; set; }
        public long ParentGasUsed { get; set; }
        public long ParentGasLimit { get; set; }
        public ulong? ParentExcessBlobGas { get; set; }
        public ulong? CurrentExcessBlobGas { get; set; }
        public ulong? ParentBlobGasUsed { get; set; }
        public Dictionary<string, Hash256> BlockHashes { get; set; } = [];
    }
}
