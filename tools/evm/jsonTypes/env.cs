using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace JsonTypes
{
    public partial class Env
    {
        public Address CurrentCoinbase { get; set; } = Address.Zero;
        public string CurrentGasLimit { get; set; } = "0x3000000000";
        //Both can be either in hex or decimal form. Conversion issue if in hex
        public ulong CurrentTimestamp { get; set; } = 0;
        public long CurrentNumber { get; set; } = 0;

        public string[] Withdrawals { get; set; } = [];

        //optional
        public Hash256 PreviousHash { get; set; } = Keccak.Zero;
        public UInt64 CurrentDataGasUsed { get; set; } = 0;
        public string ParentTimestamp { get; set; } = "0";
        public string ParentDifficulty { get; set; } = "0";
        public string CurrentDifficulty { get; set; } = "0";
        public Hash256 ParentUncleHash { get; set; } = Keccak.Zero;
        public Hash256 ParentBeaconBlockRoot { get; set; } = Keccak.Zero;
        public Hash256 CurrentRandom { get; set; } = Keccak.Zero;
        public string ParentBaseFee { get; set; } = "0x0";
        public string? ParentGasUsed { get; set; }
        public string? ParentGasLimit { get; set; }
        public ulong? ParentExcessBlobGas { get; set; }
        public ulong? ParentBlobGasUsed { get; set; }
        public Hash256[] BlockHashes { get; set; } = [];


    }
}
