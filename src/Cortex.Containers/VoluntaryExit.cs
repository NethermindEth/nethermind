namespace Cortex.Containers
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
    }
}
