namespace Nevermind.Core
{
    public class Transaction
    {
        public long Nonce { get; set; }

        public long GasPrice { get; set; }

        public long GasLimit { get; set; }

        public Address To { get; set; }

        public long Value { get; set; }

        public Signature Signature { get; set; }

        public bool IsSigned => Signature != null;

        public byte[] Collapse()
        {
            return RecursiveLengthPrefix.Serialize(Nonce, GasPrice, GasLimit);
        }
    }
}