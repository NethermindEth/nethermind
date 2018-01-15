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
            var version = ((int) ProtocolVersion.EthereumMainnet).ToString();
            return ResultWrapper<string>.Success(version);
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