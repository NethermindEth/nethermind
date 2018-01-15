using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public interface INetModule : IModule
    {
        ResultWrapper<string> net_version();
        ResultWrapper<bool> net_listening();
        ResultWrapper<Quantity> net_peerCount();
    }
}