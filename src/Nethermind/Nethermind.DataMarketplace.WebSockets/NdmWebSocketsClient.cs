//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.DataMarketplace.WebSockets
{
    public class NdmWebSocketsClient : SocketClient
    {
        private readonly INdmDataPublisher _dataPublisher;

        public NdmWebSocketsClient(string clientName, ISocketHandler handler, INdmDataPublisher dataPublisher, IJsonSerializer jsonSerializer) 
            :base(clientName, handler, jsonSerializer)
        {
            _dataPublisher = dataPublisher;
        }

        public override Task ProcessAsync(Memory<byte> data)
        {
            if (data.Length == 0)
            {
                return Task.CompletedTask;
            }

            (Keccak? dataAssetId, string? headerData) = GetDataInfo(data.ToArray());
            if (dataAssetId is null || string.IsNullOrWhiteSpace(headerData))
            {
                return Task.CompletedTask;
            }

            _dataPublisher.Publish(new DataAssetData(dataAssetId, headerData));

            return Task.CompletedTask;
        }

        private static (Keccak? dataAssetId, string? data) GetDataInfo(byte[] bytes)
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

            return dataAssetId.Length != 64 ? (null, null) : (new Keccak(dataAssetId), data);
        }
    }
}
