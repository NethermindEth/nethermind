using System.Threading.Tasks;

namespace Nethermind.Grpc
{
    public interface IGrpcServer
    {
        Task PublishAsync<T>(T data, string client) where T : class;
    }
}