// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL.L1Bridge;

public class EthereumBeaconApi : IBeaconApi
{
    private readonly HttpClient _client;
    private readonly IJsonSerializer _jsonSerializer;
    private readonly IEthereumEcdsa _ecdsa;
    private readonly ILogger _logger;

    private const int BeaconApiRetryDelayMilliseconds = 1000;

    public EthereumBeaconApi(Uri beaconApiUri, IJsonSerializer jsonSerializer, IEthereumEcdsa ecdsa, ILogger logger)
    {
        _client = new HttpClient { BaseAddress = beaconApiUri };
        _jsonSerializer = jsonSerializer;
        _ecdsa = ecdsa;
        _logger = logger;
    }

    public async Task<BlobSidecar[]> GetBlobSidecars(ulong slot, int indexFrom, int indexTo, CancellationToken cancellationToken)
    {
        string req =
            $"/eth/v1/beacon/blob_sidecars/{slot}?indices={string.Join(',', Enumerable.Range(indexFrom, indexTo - indexFrom + 1))}";
        GetBlobSidecarsResponse? data = await GetData<GetBlobSidecarsResponse>(req, cancellationToken);
        while (data is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Unable to get blob sidecars for slot {slot}, index from {indexFrom}, index to {indexTo}.");
            await Task.Delay(BeaconApiRetryDelayMilliseconds, cancellationToken);
            data = await GetData<GetBlobSidecarsResponse>(req, cancellationToken);
        }
        if (indexTo - indexFrom + 1 != data.Value.Data.Length)
        {
            _logger.Warn($"Invalid number of blobs in slot {slot}. Expected {indexTo - indexFrom + 1}. Got {data.Value.Data.Length}");
            throw new Exception($"Blob sidecars are unavailable");
        }
        for (int i = 0; i < data.Value.Data.Length; ++i)
        {
            data.Value.Data[i].BlobVersionedHash = (new byte[] { 1 })
                .Concat(SHA256.HashData(data.Value.Data[i].KzgCommitment)[1..]).ToArray();
        }

        return data.Value.Data;
    }

    private async Task<T?> GetData<T>(string uri, CancellationToken cancellationToken)
    {
        try
        {
            HttpResponseMessage response = await _client.GetAsync(uri, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (_logger.IsWarn)
                {
                    _logger.Warn($"Unsuccessful {uri} request. Status code: {response.StatusCode}");
                }

                return default;
            }

            if (_logger.IsDebug)
                _logger.Debug($"GetData<{typeof(T)}({uri}) result: {await response.Content.ReadAsStringAsync(cancellationToken)}");

            T decoded =
                _jsonSerializer.Deserialize<T>(await response.Content.ReadAsStreamAsync(cancellationToken));

            return decoded;
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Beacon API request exception({uri}): {e.Message}");
        }

        return default;
    }

#pragma warning disable 0649
    private struct GetBlobSidecarsResponse
    {
        public BlobSidecar[] Data;
    }
#pragma warning restore 0649
}
