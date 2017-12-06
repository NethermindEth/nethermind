using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public interface INetModule : IModule
    {
        string net_version();
        bool net_listening();
        Quantity net_peerCount();
    }
}