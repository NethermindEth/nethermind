namespace JsonTypes
{
    using Alloc = Dictionary<String, AccountState>;
    public partial class AccountState
    {
        // Uint256 seems to have issues with automatic json decoding
        public string Balance { get; set; } = "0x0";
        public string Code { get; set; } = "0x0";
        public string Nonce { get; set; } = "0x0";
        public Dictionary<String, String> Storage { get; set; } = new Dictionary<String, String>();
        public byte[] SecretKey;
    }
}
