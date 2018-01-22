using System.Threading.Tasks;

namespace Nevermind.Discovery
{
    public interface INodesLocator
    {
        Task LocateNodes();
    }
}