using Nevermind.Core;
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public class NetModule : ModuleBase, INetModule
    {
        public NetModule(ILogger logger) : base(logger)
        {
        }

        public string net_version()
        {
            return ((int) ProtocolVersion.EthereumMainnet).ToString();
        }

        public bool net_listening()
        {
            return true;
        }

        public Quantity net_peerCount()
        {
            return new Quantity(65);
        }

        public void Initialize()
        {
            throw new System.NotImplementedException();
        }
    }
}