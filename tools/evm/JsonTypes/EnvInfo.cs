using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Evm.JsonTypes
{
    public class EnvInfo
    {
        public Address CurrentCoinbase { get; set; } = Address.Zero;
        public string CurrentGasLimit { get; set; } = "0x3000000000";
        public ulong CurrentTimestamp { get; set; } = 0;
        public long CurrentNumber { get; set; } = 0;

        public string[] Withdrawals { get; set; } = [];

        public Hash256 PreviousHash { get; set; } = Keccak.Zero;
        public UInt64 CurrentDataGasUsed { get; set; } = 0;
        public string ParentTimestamp { get; set; } = "0";
        public string ParentDifficulty { get; set; } = "0";
        public string CurrentBaseFee { get; set; } = "0x";
        public string CurrentDifficulty { get; set; } = "0";
        public Hash256 ParentUncleHash { get; set; } = Keccak.Zero;
        public Hash256 ParentBeaconBlockRoot { get; set; } = Keccak.Zero;
        public Hash256 CurrentRandom { get; set; } = Keccak.Zero;
        public UInt256 ParentBaseFee { get; set; } = UInt256.Zero;
        public string? ParentGasUsed { get; set; }
        public string? ParentGasLimit { get; set; }
        public ulong? ParentExcessBlobGas { get; set; }
        public ulong? ParentBlobGasUsed { get; set; }
        public Dictionary<string, Hash256> BlockHashes { get; set; } = [];
    }
}
