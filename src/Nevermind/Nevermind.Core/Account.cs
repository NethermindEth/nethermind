namespace Nevermind.Core
{
    public class Account
    {
        public long Nonce { get; set; }
        public long Balance { get; set; }
        public Keccak StorageRoot { get; set; }
        public Keccak CodeHash { get; set; }
    }
}