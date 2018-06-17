using System.Threading.Tasks;

namespace Nethermind.Network.P2P
{
    public interface IP2PMessageSender
    {
        Task<bool> SendPing();
    }
}