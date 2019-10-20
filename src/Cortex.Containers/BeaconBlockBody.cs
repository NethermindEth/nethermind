using System.Collections.Generic;

namespace Cortex.Containers
{
    public class BeaconBlockBody
    {
        public BeaconBlockBody()
        {
            Deposits = new List<Deposit>();
            Eth1Data = new Eth1Data(Hash32.Zero, 0);
            Graffiti = new Bytes32();
            RandaoReveal = new BlsSignature();
        }

        public BeaconBlockBody(BlsSignature randaoReveal)
        {
            Deposits = new List<Deposit>();
            Eth1Data = new Eth1Data(Hash32.Zero, 0);
            Graffiti = new Bytes32();
            RandaoReveal = randaoReveal;
        }

        public IList<Deposit> Deposits { get; }
        public Eth1Data Eth1Data { get; }
        public Bytes32 Graffiti { get; }
        public BlsSignature RandaoReveal { get; }
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
