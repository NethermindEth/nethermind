using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
{
    public class VoluntaryExit
    {
        public VoluntaryExit(Epoch epoch, ValidatorIndex validatorIndex, BlsSignature signature)
        {
            Epoch = epoch;
            ValidatorIndex = validatorIndex;
            Signature = signature;
        }

        /// <summary>
        /// Earliest epoch when voluntary exit can be processed
        /// </summary>
        public Epoch Epoch { get; }

        public BlsSignature Signature { get; private set; }

        public ValidatorIndex ValidatorIndex { get; }

        public void SetSignature(BlsSignature signature)
        {
            Signature = signature;
        }

        public override string ToString()
        {
            return $"V:{ValidatorIndex} E:{Epoch}";
        }
    }
}
