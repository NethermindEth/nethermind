using System.Collections.Generic;
using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class BeaconBlockBodyExtensions
    {
        public static SszContainer ToSszContainer(this BeaconBlockBody item, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            return new SszContainer(GetValues(item, maxOperationsPerBlock));
        }

        private static IEnumerable<SszElement> GetValues(BeaconBlockBody item, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            yield return item.RandaoReveal.ToSszBasicVector();
            yield return item.Eth1Data.ToSszContainer();
            yield return item.Graffiti.ToSszBasicVector();
            // Operations
            //yield return item.ProposerSlashings.ToSszList(MAX_PROPOSER_SLASHINGS);
            //yield return item.AttesterSlashings.ToSszList(MAX_ATTESTER_SLASHINGS);
            //yield return item.Attestations.ToSszList(MAX_ATTESTATIONS);
            yield return item.Deposits.ToSszList(maxOperationsPerBlock.MaxDeposits);
            //yield return item.VoluntaryExits.ToSszList(MAX_VOLUNTARY_EXITS);
            //yield return item.Transfers.ToSszList(MAX_TRANSFERS);
        }
    }
}
