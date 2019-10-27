using System;
using System.Collections.Generic;

namespace Cortex.Containers
{
    public class BeaconBlockBody
    {
        private readonly List<Deposit> _deposits;

        public BeaconBlockBody(
            BlsSignature randaoReveal,
            Eth1Data eth1Data,
            Bytes32 graffiti,
            IEnumerable<Deposit> deposits)
        {
            RandaoReveal = randaoReveal;
            Eth1Data = eth1Data;
            Graffiti = graffiti;
            _deposits = new List<Deposit>(deposits);
        }

        public BeaconBlockBody()
        {
            RandaoReveal = new BlsSignature();
            Eth1Data = new Eth1Data(Hash32.Zero, 0);
            Graffiti = new Bytes32();
            _deposits = new List<Deposit>();
        }

        public IReadOnlyList<Deposit> Deposits { get { return _deposits; } }
        public Eth1Data Eth1Data { get; }
        public Bytes32 Graffiti { get; }
        public BlsSignature RandaoReveal { get; private set; }

        public void AddAttestations(Attestation attestation)
        {
            throw new NotImplementedException();
        }

        public void SetRandaoReveal(BlsSignature randaoReveal)
        {
            RandaoReveal = randaoReveal;
        }

        // Operations

        /*
        public IList<ProposerSlashing> ProposerSlashings { get; }

        public IList<AttesterSlashing> AttestersSlashings { get; }

        public IList<Attestation> Attestations { get; }
        */
        /*
        public IList<VoluntaryExit> VoluntaryExits { get; }

        public IList<Transfer> Transfers { get; }
        */
    }
}
