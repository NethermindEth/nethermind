using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode.Tests
{
    public static class TestConfiguration
    {
        public static void GetMinimalConfiguration(
            out ChainConstants chainConstants,
            out IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            out IOptionsMonitor<GweiValues> gweiValueOptions,
            out IOptionsMonitor<InitialValues> initialValueOptions,
            out IOptionsMonitor<TimeParameters> timeParameterOptions,
            out IOptionsMonitor<StateListLengths> stateListLengthOptions,
            out IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions)
        {
            chainConstants = new ChainConstants();
            miscellaneousParameterOptions = TestOptionsMonitor.Create(new MiscellaneousParameters()
            {
                ShardCount = new Shard(8),
                TargetCommitteeSize = 4,
                MaximumValidatorsPerCommittee = 4096,
                ShuffleRoundCount = 10,
                MinimumGenesisActiveValidatorCount = 64,
                MinimumGenesisTime = 1578009600 // Jan 3, 2020
            });
            gweiValueOptions = TestOptionsMonitor.Create(new GweiValues()
            {
                MaximumEffectiveBalance = new Gwei(((ulong)1 << 5) * 1000 * 1000 * 1000),
                EffectiveBalanceIncrement = new Gwei(1000 * 1000 * 1000)
            });
            initialValueOptions = TestOptionsMonitor.Create(new InitialValues()
            {
                GenesisEpoch = new Epoch(0),
                BlsWithdrawalPrefix = 0x00
            });
            timeParameterOptions = TestOptionsMonitor.Create(new TimeParameters()
            {
                SlotsPerEpoch = new Slot(8),
                MinimumSeedLookahead = new Epoch(1),
                SlotsPerHistoricalRoot = new Slot(64)
            });
            stateListLengthOptions = TestOptionsMonitor.Create(new StateListLengths()
            {
                EpochsPerHistoricalVector = new Epoch(64),
                ValidatorRegistryLimit = (ulong)1 << 40
            });
            maxOperationsPerBlockOptions = TestOptionsMonitor.Create(new MaxOperationsPerBlock()
            {
                MaximumAttestations = 128,
                MaximumDeposits = 16
            });
        }
    }
}
