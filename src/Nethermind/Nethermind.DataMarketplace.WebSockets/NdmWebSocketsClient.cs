/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.WebSockets;

namespace Nethermind.DataMarketplace.WebSockets
{
    public class NdmWebSocketsClient : IWebSocketsClient
    {
        private readonly IWebSocketsClient _client;
        private readonly INdmDataPublisher _dataPublisher;
        public string Id => _client.Id;
        public string Client { get; }

        public NdmWebSocketsClient(IWebSocketsClient client, INdmDataPublisher dataPublisher)
        {
            _client = client;
            _dataPublisher = dataPublisher;
            Client = client.Client;
        }

        public Task ReceiveAsync(byte[] data)
        {
            if (data is null || data.Length == 0)
            {
                return Task.CompletedTask;
            }

            var (dataAssetId, headerData) = GetDataInfo(data);
            if (dataAssetId is null || string.IsNullOrWhiteSpace(headerData))
            {
                return Task.CompletedTask;
            }

            _dataPublisher.Publish(new DataAssetData(dataAssetId, headerData));

            return Task.CompletedTask;
        }

        private static (Keccak dataAssetId, string data) GetDataInfo(byte[] bytes)
        {
            var request = Encoding.UTF8.GetString(bytes);
            var parts = request.Split('|');

            if (!parts.Any() || parts.Length != 3)
            {
                return (null, null);
            }

            var dataAssetId = parts[0];
            var extension = parts[1];
            var data = parts[2];

            return string.IsNullOrWhiteSpace(dataAssetId) ? (null, null) : (new Keccak(dataAssetId), data);
        }

        public Task SendRawAsync(string data) => _client.SendRawAsync(data);
        public Task SendAsync(WebSocketsMessage message) => _client.SendAsync(message);
    }
}