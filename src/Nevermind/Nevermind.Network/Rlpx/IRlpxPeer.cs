using System.Threading.Tasks;

namespace Nevermind.Network.Rlpx
{
    public interface IRlpxPeer
    {
        Task Shutdown();
        Task Init();
    }
}