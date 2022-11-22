// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            : base(clientName, handler, jsonSerializer)
        {
            _dataPublisher = dataPublisher;
        }

        public override Task ProcessAsync(ArraySegment<byte> data)
        {
            if (data.Count == 0)
            {
                return Task.CompletedTask;
            }

            (Keccak? dataAssetId, string? headerData) = GetDataInfo(data);
            if (dataAssetId is null || string.IsNullOrWhiteSpace(headerData))
            {
                return Task.CompletedTask;
            }

            _dataPublisher.Publish(new DataAssetData(dataAssetId, headerData));

            return Task.CompletedTask;
        }

        private static (Keccak? dataAssetId, string? data) GetDataInfo(ArraySegment<byte> bytes)
        {
            var request = Encoding.UTF8.GetString(bytes.Array!, bytes.Offset, bytes.Count);
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
