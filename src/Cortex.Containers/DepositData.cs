namespace Cortex.Containers
{
    public class DepositData
    {
        public DepositData()
        {
            PublicKey = new BlsPublicKey();
            Signature = new BlsSignature();
            WithdrawalCredentials = new Hash32();
        }

        public Gwei Amount { get; }

        public BlsPublicKey PublicKey { get; }

        public BlsSignature Signature { get; }

        public Hash32 WithdrawalCredentials { get; }
    }
}
