namespace Nethermind.BeaconNode.Containers
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

        public override string ToString()
        {
            return $"A1:({Attestation1}) A2:({Attestation2})";
        }
    }
}
