namespace JsonTypes
{
    public partial class Env
    {
        //TODO: Figure out which are optional and which fields are missing
        public string? CurrentCoinbase { get; set; }
        public string? CurrentNumber { get; set; }
        public string? CurrentTimestamp { get; set; }
        public string? CurrentGasLimit { get; set; }
        public string? PreviousHash { get; set; }
        public string? CurrentDataGasUsed { get; set; }
        public string? ParentTimestamp { get; set; }
        public string? ParentDifficulty { get; set; }
        public string? ParentUncleHash { get; set; }
        public string? ParentBeaconBlockRoot { get; set; }
        public string? CurrentRandom { get; set; }
        public string[] Withdrawals { get; set; } = [];
        public string? ParentBaseFee { get; set; }
        public string? ParentGasUsed { get; set; }
        public string? ParentGasLimit { get; set; }
        public string? ParentExcessBlobGas { get; set; }
        public string? ParentBlobGasUsed { get; set; }
        public string[] BlockHashes { get; set; } = [];
    }
}
