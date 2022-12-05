// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Grpc;

namespace Nethermind.DataMarketplace.Channels.Grpc
{
    public class GrpcNdmConsumerChannel : INdmConsumerChannel
    {
        private readonly IGrpcServer _grpcServer;
        public NdmConsumerChannelType Type => NdmConsumerChannelType.Grpc;

        public GrpcNdmConsumerChannel(IGrpcServer grpcServer)
        {
            _grpcServer = grpcServer;
        }

        public Task PublishAsync(Keccak depositId, string client, string data)
            => _grpcServer.PublishAsync(new { depositId, data }, client);
    }
}
