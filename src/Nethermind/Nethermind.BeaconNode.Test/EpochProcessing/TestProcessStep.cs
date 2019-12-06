namespace Nethermind.BeaconNode.Tests.EpochProcessing
{
    public enum TestProcessStep
    {
        None = 0,
        ProcessJustificationAndFinalization,
        //ProcessCrosslinks,
        ProcessRewardsAndPenalties,
        ProcessRegistryUpdates,
        ProcessSlashings,
        ProcessFinalUpdates,
    }
}
