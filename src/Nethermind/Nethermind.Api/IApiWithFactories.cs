using Nethermind.Api.Factories;

namespace Nethermind.Api;

public interface IApiWithFactories : IApiWithStores
{
    IBlockProcessorFactory BlockProcessorFactory { get; set; }
}