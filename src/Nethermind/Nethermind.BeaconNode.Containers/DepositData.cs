using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Hash32 = Nethermind.Core2.Types.Hash32;

namespace Nethermind.BeaconNode.Containers
{
    public class DepositData
    {
        public DepositData(BlsPublicKey publicKey, Hash32 withdrawalCredentials, Gwei amount)
        {
            PublicKey = publicKey;
            WithdrawalCredentials = withdrawalCredentials;
            Amount = amount;
            Signature = BlsSignature.Empty;
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
            return $"P:{PublicKey.ToString().Substring(0, 12)} A:{Amount}";
        }
    }
}
