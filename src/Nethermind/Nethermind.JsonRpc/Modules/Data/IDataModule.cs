using System.Numerics;

namespace Nethermind.JsonRpc.Modules.Data
{
    public interface IDataModule : IModule
    {
        ResultWrapper<string> data_streamBlocks(BigInteger startBlockNumber, BigInteger endBlockNumber);
    }
}