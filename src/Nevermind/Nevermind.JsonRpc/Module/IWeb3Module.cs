using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public interface IWeb3Module : IModule
    {
        string web3_clientVersion();
        Data web3_sha3(Data data);
    }
}