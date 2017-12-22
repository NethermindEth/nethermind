using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public interface IWeb3Module : IModule
    {
        ResultWrapper<string> web3_clientVersion();
        ResultWrapper<Data> web3_sha3(Data data);
    }
}