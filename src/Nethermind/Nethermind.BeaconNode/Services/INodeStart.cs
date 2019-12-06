using System.Threading.Tasks;

namespace Nethermind.BeaconNode.Services
{
    public interface INodeStart
    {
        Task InitializeNodeAsync();
    }
}
