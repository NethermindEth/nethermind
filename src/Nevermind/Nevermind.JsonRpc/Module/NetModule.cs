using Nevermind.Core;
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public class NetModule : ModuleBase, INetModule
    {
        public NetModule(ILogger logger, IConfigurationProvider configurationProvider) : base(logger, configurationProvider)
        {
        }

        public ResultWrapper<string> net_version()
        {
            return ResultWrapper<string>.Success(EthereumNetwork.Main.GetNetworkId().ToString());
        }

        public ResultWrapper<bool> net_listening()
        {
            return ResultWrapper<bool>.Success(false);
        }

        public ResultWrapper<Quantity> net_peerCount()
        {
            throw new System.NotImplementedException();
        }
    }
}