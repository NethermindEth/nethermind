// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.PubSub;

namespace Nethermind.Grpc.Producers
{
    public class GrpcPublisher : IPublisher
    {
        private readonly IGrpcServer _server;

        public GrpcPublisher(IGrpcServer server)
        {
            _server = server;
        }

        public Task PublishAsync<T>(T data) where T : class =>
            _server.PublishAsync(data, string.Empty);

        public void Dispose()
        {
        }
    }
}
