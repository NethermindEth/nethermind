namespace Cortex.Containers
{
    public class AttesterSlashing
    {
        public AttesterSlashing(
            IndexedAttestation attestation1,
            IndexedAttestation attestation2)
        {
            Attestation1 = attestation1;
            Attestation2 = attestation2;
        }

        public IndexedAttestation Attestation1 { get; }
        public IndexedAttestation Attestation2 { get; }
    }
}
