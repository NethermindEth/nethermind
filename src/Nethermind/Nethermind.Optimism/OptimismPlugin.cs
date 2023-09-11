using System.Threading.Tasks;
using DotNetty.Common.Internal.Logging;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;

namespace Nethermind.Optimism;

public class OptimismPlugin : MergePlugin, IConsensusPlugin, IInitializationPlugin
{
    public override string Name => "Optimism";

    public override string Description => "Optimism support for Nethermind";

    protected override bool MergeEnabled => ShouldRunSteps(_api);

    public string SealEngineType => Core.SealEngineType.Optimism;

    public IBlockProductionTrigger DefaultBlockProductionTrigger =>
        throw new System.NotImplementedException("Block producer is not supported for Optimism Pre-Bedrock.");

    public Task<IBlockProducer> InitBlockProducer(IBlockProductionTrigger? blockProductionTrigger = null, ITxSource? additionalTxSource = null) =>
        throw new System.NotImplementedException("Block producer is not supported for Optimism Pre-Bedrock.");

    public bool ShouldRunSteps(INethermindApi api) => api.ChainSpec.SealEngineType == SealEngineType;

    protected override PostMergeBlockProducerFactory CreateBlockProducerFactory()
    {
        return new OptimismPostMergeBlockProducerFactory(
            _api.SpecProvider!,
            _api.SealEngine,
            _manualTimestamper!,
            _blocksConfig,
            _api.LogManager);
    }

    public override Task Init(INethermindApi nethermindApi)
    {
        return base.Init(nethermindApi).ContinueWith(_ =>
        {
            if (!MergeEnabled)
                return;

            _logger.Info("Optimism plugin is enabled.");

            _engineRpcModuleFactory = new OptimismEngineRpcModuleFactory(_api.SpecProvider!, _api.LogManager);
        });
    }
}
