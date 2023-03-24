using Nethermind.Api.Factories;
using Nethermind.Consensus.Processing;

namespace Nethermind.Api;

public interface IApiWithFactories : IApiWithStores
{
    IApiComponentFactory<IBlockProcessor> BlockProcessorFactory { get; set; }
}