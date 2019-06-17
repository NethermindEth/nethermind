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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.WebSockets;

namespace Nethermind.DataMarketplace.WebSockets
{
    public class NdmWebSocketsModule : IWebSocketsModule
    {
        private readonly INdmConsumerChannelManager _consumerChannelManager;
        private readonly INdmDataPublisher _dataPublisher;
        private ClientType _clientType;
        private Keccak _depositId;
        private WebSocketsNdmConsumerChannel _channel;
        
        public string Name { get; } = "ndm";

        public NdmWebSocketsModule(INdmConsumerChannelManager consumerChannelManager, INdmDataPublisher dataPublisher)
        {
            _consumerChannelManager = consumerChannelManager;
            _dataPublisher = dataPublisher;
        }

        public bool TryInit(HttpRequest request)
        {
            if (!request.Query.TryGetValue("type", out var type) || string.IsNullOrWhiteSpace(type))
            {
                return false;
            }

            if (!Enum.TryParse<ClientType>(type, true, out var clientType))
            {
                return false;
            }

            _clientType = clientType;
            if (_clientType == ClientType.Provider)
            {
                return true;
            }

            if (!request.Query.TryGetValue("deposit", out var deposit) ||
                string.IsNullOrWhiteSpace(deposit))
            {
                return false;
            }

            _depositId = Keccak.TryParse(deposit);
            
            return !(_depositId is null);
        }

        public void AddClient(IWebSocketsClient client)
        {
            _channel = new WebSocketsNdmConsumerChannel(client, _depositId);
            _consumerChannelManager.Add(_channel);
        }

        public Task ExecuteAsync(IWebSocketsClient client, byte[] data)
        {
            if (_clientType == ClientType.Consumer)
            {
                return Task.CompletedTask;
            }

            if (data is null || data.Length == 0)
            {
                return Task.CompletedTask;
            }
            
            var (dataHeaderId, headerData) = GetDataInfo(data);
            if (dataHeaderId is null || string.IsNullOrWhiteSpace(headerData))
            {
                return Task.CompletedTask;
            }

            var dataResult = new Dictionary<string, string>
            {
                [string.Empty] = headerData
            };
            _dataPublisher.Publish(new DataHeaderData(dataHeaderId, dataResult));

            return Task.CompletedTask;
        }

        public void Cleanup(IWebSocketsClient client)
        {
            _consumerChannelManager.Remove(_channel);
        }

        private static (Keccak dataHeaderId, string data) GetDataInfo(byte[] bytes)
        {
            var request = Encoding.UTF8.GetString(bytes);
            var parts = request.Split('|');

            if (!parts.Any() || parts.Length != 3)
            {
                return (null, null);
            }

            var dataHeaderId = parts[0];
            var extension = parts[1];
            var data = parts[2];

            return string.IsNullOrWhiteSpace(dataHeaderId) ? (null, null) : (new Keccak(dataHeaderId), data);
        }
        
        private enum ClientType
        {
            Consumer,
            Provider
        }
    }
}