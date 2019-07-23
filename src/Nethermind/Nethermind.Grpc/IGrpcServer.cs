using System.Threading.Tasks;

namespace Nethermind.Grpc
{
    public interface IGrpcServer
    {
        Task PublishAsync<T>(T data) where T : class;
    }
}