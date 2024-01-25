using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace JsonTypes
{
    public partial class Env
    {
        //TODO: Figure out which are optional and which fields are missing
        public Address CurrentCoinbase { get; set; } = new Address(new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000"));
        public long CurrentNumber { get; set; } = 0;
        public ulong CurrentTimestamp { get; set; } = 0;
        public string CurrentGasLimit { get; set; } = "0x3000000000";
        public Hash256 PreviousHash { get; set; } = new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000");
        public UInt64 CurrentDataGasUsed { get; set; } = 0;
        public string ParentTimestamp { get; set; } = "0";
        public string ParentDifficulty { get; set; } = "0";
        public string CurrentDifficulty { get; set; } = "0";
        public Hash256 ParentUncleHash { get; set; } = new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000");
        public Hash256 ParentBeaconBlockRoot { get; set; } = new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000");
        public Hash256 CurrentRandom { get; set; } = new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000");
        public string[] Withdrawals { get; set; } = [];
        public string ParentBaseFee { get; set; } = "0x0";
        public string? ParentGasUsed { get; set; }
        public string? ParentGasLimit { get; set; }
        public string? ParentExcessBlobGas { get; set; }
        public string? ParentBlobGasUsed { get; set; }
        public Hash256[] BlockHashes { get; set; } = [];

        public void convert(Env e){

        }

    }
}
