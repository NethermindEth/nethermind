using System.Collections.Generic;

using BlsSignature = System.Byte; // Byte96

namespace Cortex.Containers
{
    public class BeaconBlockBody
    {
        public BeaconBlockBody()
        {
        }

        public BeaconBlockBody(BlsSignature[] randaoReveal)
        {
            RandaoReveal = randaoReveal;
        }

        public IList<Deposit> Deposits { get; }
        public Eth1Data Eth1Data { get; }
        public byte[] Graffiti { get; }
        public BlsSignature[] RandaoReveal { get; }
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
