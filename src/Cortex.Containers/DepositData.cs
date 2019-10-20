namespace Cortex.Containers
{
    public class DepositData
    {
        public DepositData(BlsPublicKey publicKey, Hash32 withdrawalCredentials, Gwei amount)
        {
            PublicKey = publicKey;
            WithdrawalCredentials = withdrawalCredentials;
            Amount = amount;
            Signature = new BlsSignature();
        }

        public Gwei Amount { get; }

        public BlsPublicKey PublicKey { get; }

        public BlsSignature Signature { get; private set; }

        public Hash32 WithdrawalCredentials { get; }

        public void SetSignature(BlsSignature signature)
        {
            Signature = signature;
        }

        public override string ToString()
        {
            return $"P:{PublicKey.ToString().Substring(0, 16)} A:{Amount}";
        }
    }
}
