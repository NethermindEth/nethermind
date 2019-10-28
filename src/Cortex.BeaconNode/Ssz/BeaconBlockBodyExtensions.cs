using System;
using System.Collections.Generic;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class BeaconBlockBodyExtensions
    {
        public static Hash32 HashTreeRoot(this BeaconBlockBody item, MiscellaneousParameters miscellaneousParameters, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            var tree = new SszTree(item.ToSszContainer(miscellaneousParameters, maxOperationsPerBlock));
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this BeaconBlockBody item, MiscellaneousParameters miscellaneousParameters, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            return new SszContainer(GetValues(item, miscellaneousParameters, maxOperationsPerBlock));
        }

        private static IEnumerable<SszElement> GetValues(BeaconBlockBody item, MiscellaneousParameters miscellaneousParameters, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            yield return item.RandaoReveal.ToSszBasicVector();
            yield return item.Eth1Data.ToSszContainer();
            yield return item.Graffiti.ToSszBasicVector();
            // Operations
            //yield return item.ProposerSlashings.ToSszList(MAX_PROPOSER_SLASHINGS);
            //yield return item.AttesterSlashings.ToSszList(MAX_ATTESTER_SLASHINGS);
            yield return item.Attestations.ToSszList(maxOperationsPerBlock.MaximumAttestations, miscellaneousParameters);
            yield return item.Deposits.ToSszList(maxOperationsPerBlock.MaximumDeposits);
            //yield return item.VoluntaryExits.ToSszList(MAX_VOLUNTARY_EXITS);
            //yield return item.Transfers.ToSszList(MAX_TRANSFERS);
        }
    }
}
