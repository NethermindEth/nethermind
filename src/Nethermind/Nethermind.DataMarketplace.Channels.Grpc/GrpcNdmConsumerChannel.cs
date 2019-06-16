using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Grpc;

namespace Nethermind.DataMarketplace.Channels.Grpc
{
    public class GrpcNdmConsumerChannel : INdmConsumerChannel
    {
        private readonly IGrpcService _grpcService;
        public NdmConsumerChannelType Type => NdmConsumerChannelType.Grpc;

        public GrpcNdmConsumerChannel(IGrpcService grpcService)
        {
            _grpcService = grpcService;
        }

        public Task PublishAsync(Keccak depositId, string data) => _grpcService.SendNdmDataAsync(depositId, data);
    }
}