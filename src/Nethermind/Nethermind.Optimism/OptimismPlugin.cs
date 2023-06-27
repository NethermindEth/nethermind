using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;

namespace Nethermind.Optimism;

public class OptimismPlugin : MergePlugin, IInitializationPlugin
{
    public override string Name => "Optimism";

    public override string Description => "Optimism support for Nethermind";

    protected override bool MergeEnabled => ShouldRunSteps(_api);

    public bool ShouldRunSteps(INethermindApi api) => api.Config<IOptimismConfig>().Enabled; // we can also make it chain spec based

    protected override PostMergeBlockProducerFactory CreateBlockProducerFactory()
    {
        return new OptimismPostMergeBlockProducerFactory(
            _api.SpecProvider!,
            _api.SealEngine,
            _manualTimestamper!,
            _blocksConfig,
            _api.LogManager);
    }
}
