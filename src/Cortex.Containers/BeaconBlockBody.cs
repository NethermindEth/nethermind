using System.Collections.Generic;

namespace Cortex.Containers
{
    public class BeaconBlockBody
    {
        private readonly List<Attestation> _attestations;
        private readonly List<Deposit> _deposits;

        public BeaconBlockBody(
            BlsSignature randaoReveal,
            Eth1Data eth1Data,
            Bytes32 graffiti,
            IEnumerable<Attestation> attestations,
            IEnumerable<Deposit> deposits)
        {
            RandaoReveal = randaoReveal;
            Eth1Data = eth1Data;
            Graffiti = graffiti;
            _attestations = new List<Attestation>(attestations);
            _deposits = new List<Deposit>(deposits);
        }

        public BeaconBlockBody()
        {
            RandaoReveal = new BlsSignature();
            Eth1Data = new Eth1Data(0, Hash32.Zero);
            Graffiti = new Bytes32();
            _attestations = new List<Attestation>();
            _deposits = new List<Deposit>();
        }

        public IReadOnlyList<Attestation> Attestations { get { return _attestations; } }
        public IReadOnlyList<Deposit> Deposits { get { return _deposits; } }
        public Eth1Data Eth1Data { get; }
        public Bytes32 Graffiti { get; }
        public BlsSignature RandaoReveal { get; private set; }

        public void AddAttestations(Attestation attestation)
        {
            _attestations.Add(attestation);
        }

        public void SetRandaoReveal(BlsSignature randaoReveal)
        {
            RandaoReveal = randaoReveal;
        }

        // Operations

        /*
        public IList<ProposerSlashing> ProposerSlashings { get; }

        public IList<AttesterSlashing> AttestersSlashings { get; }

        */
        /*
        public IList<VoluntaryExit> VoluntaryExits { get; }

        public IList<Transfer> Transfers { get; }
        */
    }
}
