using Microsoft.Extensions.Logging;

namespace Nethermind.BeaconNode
{
    public static class Event
    {
        // 1bxx preliminary
        public static readonly EventId WorkerStarted = new EventId(1000, nameof(WorkerStarted));
        public static readonly EventId TryGenesis = new EventId(1100, nameof(TryGenesis));
        public static readonly EventId InitializeBeaconState = new EventId(1101, nameof(InitializeBeaconState));

        // 2bxx completion
        public static readonly EventId ProcessDeposit = new EventId(2000, nameof(ProcessDeposit));
        public static readonly EventId ProcessSlots = new EventId(2001, nameof(ProcessSlots));
        public static readonly EventId ProcessSlot = new EventId(2002, nameof(ProcessSlot));
        public static readonly EventId ProcessJustificationAndFinalization = new EventId(2003, nameof(ProcessJustificationAndFinalization));
        public static readonly EventId ProcessEpoch = new EventId(2004, nameof(ProcessEpoch));
        public static readonly EventId ProcessBlock = new EventId(2005, nameof(ProcessBlock));
        public static readonly EventId ProcessBlockHeader = new EventId(2006, nameof(ProcessBlockHeader));
        public static readonly EventId ProcessRandao = new EventId(2007, nameof(ProcessRandao));
        public static readonly EventId ProcessEth1Data = new EventId(2008, nameof(ProcessEth1Data));
        public static readonly EventId ProcessOperations = new EventId(2009, nameof(ProcessOperations));
        public static readonly EventId ProcessProposerSlashing = new EventId(2010, nameof(ProcessProposerSlashing));
        public static readonly EventId ProcessAttesterSlashing = new EventId(2011, nameof(ProcessAttesterSlashing));
        public static readonly EventId ProcessAttestation = new EventId(2012, nameof(ProcessAttestation));
        public static readonly EventId ProcessVoluntaryExit = new EventId(2013, nameof(ProcessVoluntaryExit));
        public static readonly EventId ProcessRewardsAndPenalties = new EventId(2014, nameof(ProcessRewardsAndPenalties));
        public static readonly EventId ProcessFinalUpdates = new EventId(2015, nameof(ProcessFinalUpdates));
        public static readonly EventId ProcessRegistryUpdates = new EventId(2016, nameof(ProcessRegistryUpdates));
        public static readonly EventId ProcessSlashings = new EventId(2017, nameof(ProcessSlashings));

        public static readonly EventId CreateGenesisStore = new EventId(2100, nameof(CreateGenesisStore));
        public static readonly EventId WorkerStoreAvailableTickStarted = new EventId(2101, nameof(WorkerStoreAvailableTickStarted));

        // 4bxx warning
        public static readonly EventId InvalidIndexedAttestation = new EventId(4100, nameof(InvalidIndexedAttestation));

        // 5bxx error

        // 8bxx finalization

        // 9bxx critical
    }
}
