// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL;

public class EthereumBeaconApi : IBeaconApi
{
    private readonly HttpClient _client;
    private readonly IJsonSerializer _jsonSerializer;
    private readonly CancellationToken _cancellationToken;
    private readonly IEthereumEcdsa _ecdsa;
    private readonly ILogger _logger;

    public EthereumBeaconApi(Uri beaconApiUri, IJsonSerializer jsonSerializer, IEthereumEcdsa ecdsa, ILogger logger, CancellationToken cancellationToken)
    {
        _client = new HttpClient { BaseAddress = beaconApiUri };
        _jsonSerializer = jsonSerializer;
        _cancellationToken = cancellationToken;
        _ecdsa = ecdsa;
        _logger = logger;
    }

    public async Task<BeaconBlock> GetHead()
    {
        GetBlockResponse data = await GetData<GetBlockResponse>("/eth/v2/beacon/blocks/head");
        return new BeaconBlock
        {
            PayloadNumber = data.Data.Message.Body.ExecutionPayload.BlockNumber,
            SlotNumber = data.Data.Message.Slot,
            ExecutionBlockHash = data.Data.Message.Body.ExecutionPayload.BlockHash,
            Transactions = data.Data.Message.Body.ExecutionPayload.Transactions.Select(x =>
            {
                // Should we remove this and use L1 EL to retrieve data?
                var tx = Rlp.Decode<Transaction>(x);
                tx.SenderAddress = _ecdsa.RecoverAddress(tx, true);
                return tx;
            }).ToArray()
        };
    }

    public async Task<BeaconBlock> GetFinalized()
    {
        GetBlockResponse data = await GetData<GetBlockResponse>("/eth/v2/beacon/blocks/finalized");
        return new BeaconBlock
        {
            PayloadNumber = data.Data.Message.Body.ExecutionPayload.BlockNumber,
            SlotNumber = data.Data.Message.Slot,
            ExecutionBlockHash = data.Data.Message.Body.ExecutionPayload.BlockHash,
            Transactions = data.Data.Message.Body.ExecutionPayload.Transactions.Select(x =>
            {
                // Should we remove this and use L1 EL to retrieve data?
                var tx = Rlp.Decode<Transaction>(x);
                tx.SenderAddress = _ecdsa.RecoverAddress(tx, true);
                return tx;
            }).ToArray()
        };
    }

    public async Task<BlobSidecar[]> GetBlobSidecars(ulong slot)
    {
        GetBlobSidecarsResponse data = await GetData<GetBlobSidecarsResponse>($"/eth/v1/beacon/blob_sidecars/{slot}");
        for (int i = 0; i < data.Data.Length; ++i)
        {
            data.Data[i].BlobVersionedHash = (new byte[]{1}).Concat(SHA256.HashData(data.Data[i].KzgCommitment)[1..]).ToArray();
        }
        return data.Data;
    }

    private async Task<T> GetData<T>(string uri)
    {
        HttpResponseMessage response = await _client.GetAsync(uri, _cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if (_logger.IsWarn)
            {
                _logger.Warn($"Unsuccessful {uri} request");
            }
            // TODO: remove exception
            throw new Exception($"Unsuccessful {uri} request");
        }

        if (_logger.IsDebug)
        {
            _logger.Debug($"GetData<{typeof(T)}({uri}) result: {await response.Content.ReadAsStringAsync(_cancellationToken)}");
        }

        T decoded =
            _jsonSerializer.Deserialize<T>(await response.Content.ReadAsStreamAsync(_cancellationToken));

        return decoded;
    }

    // TODO: remove
#pragma warning disable 0649
    private struct GetBlockResponse
    {
        public GetBlockData Data;
    }

    // TODO: can we avoid additional structs
    private struct GetBlockData
    {
        public GetBlockMessage Message;
    }

    private struct GetBlockMessage
    {
        public ulong Slot;
        public GetBlockBody Body;
    }

    private struct GetBlockBody
    {
        [JsonPropertyName("execution_payload")]
        public GetBlockExecutionPayload ExecutionPayload;
    }

    private struct GetBlockExecutionPayload
    {
        [JsonPropertyName("block_number")]
        public ulong BlockNumber;
        [JsonPropertyName("block_hash")]
        public Hash256 BlockHash;
        public byte[][] Transactions;
    }

    private struct GetBlobSidecarsResponse
    {
        public BlobSidecar[] Data;
    }
#pragma warning restore 0649
}
