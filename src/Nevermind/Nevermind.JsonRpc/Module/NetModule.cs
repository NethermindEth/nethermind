using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public class NetModule : INetModule
    {
        public string net_version()
        {
            return ((int) ProtocolVersion.EthereumMainnet).ToString();
        }

        public bool net_listening()
        {
            throw new System.NotImplementedException();
        }

        public Quantity net_peerCount()
        {
            throw new System.NotImplementedException();
        }

        public void Initialize()
        {
            throw new System.NotImplementedException();
        }
    }
}