using System.Threading.Tasks;

namespace Nethermind.AccountAbstraction.Bundler
{
    public interface ITxBundler
    {
        void Start();
        Task StopAsync();
    }
}
