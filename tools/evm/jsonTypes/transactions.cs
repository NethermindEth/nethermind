namespace JsonTypes
{

    public partial class Transaction
    {
        //TODO: Figure out which are optional and which fields are missing
        public string? Input { get; set; }
        public string? Gas { get; set; }
        public string? Nonce { get; set; }
        public string? To { get; set; }
        public string? Value { get; set; }
        public string? V { get; set; }
        public string? R { get; set; }
        public string? S{ get; set; }
        public string? SecretKey { get; set; }
        public string? ChainId { get; set; }
        public string? Type { get; set; }
        public string? MaxFeePerGas { get; set; }
        public string? MaxPriorityFeePerGas { get; set; }
        public object[]? AccessList { get; set; }
        public bool? Protected { get; set; }
    }
}
