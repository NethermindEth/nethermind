namespace JsonTypes
{
    using Alloc = Dictionary<String, Account>;
    public partial class Account
    {
        //Maybe missing some properties
        public string Balance { get; set; } = "0x0";
        public string Code { get; set; } = "0x0";
        public string Nonce { get; set; } = "0x0";
        public Dictionary<String, String> Storage { get; set; } = new Dictionary<String, String>();
    }
}
