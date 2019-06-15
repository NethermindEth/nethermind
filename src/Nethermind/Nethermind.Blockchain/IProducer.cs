using System.Threading.Tasks;

namespace Nethermind.Blockchain
{
    public interface IProducer
    {
        Task InitAsync();
        Task PublishAsync<T>(T data) where T : class;
        Task CloseAsync();
    }
}