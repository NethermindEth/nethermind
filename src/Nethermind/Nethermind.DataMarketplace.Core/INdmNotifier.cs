using System.Threading.Tasks;

namespace Nethermind.DataMarketplace.Core
{
    public interface INdmNotifier
    {
        Task NotifyAsync(object data);
    }
}