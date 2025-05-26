// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Optimism.CL.L1Bridge;

public class EthereumBeaconApi : IBeaconApi
{
    private readonly HttpClient _client;
    private readonly IJsonSerializer _jsonSerializer;
    private readonly IEthereumEcdsa _ecdsa;
    private readonly ILogger _logger;

    private const int BeaconApiRetryDelayMilliseconds = 1000;

    public EthereumBeaconApi(Uri beaconApiUri, IJsonSerializer jsonSerializer, IEthereumEcdsa ecdsa, ILogManager logManager)
    {
        _client = new HttpClient { BaseAddress = beaconApiUri };
        _jsonSerializer = jsonSerializer;
        _ecdsa = ecdsa;
        _logger = logManager.GetClassLogger();
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
        if (indexTo - indexFrom + 1 != data.Data.Length)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid number of blobs in slot {slot}. Expected {indexTo - indexFrom + 1}. Got {data.Data.Length}");
            throw new Exception($"Blob sidecars are unavailable");
        }
        return data.Data;
    }

    private async Task<T?> GetData<T>(string uri, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _client.GetAsync(uri, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (_logger.IsWarn)
                {
                    _logger.Warn($"Unsuccessful {uri} request. Status code: {response.StatusCode}");
                }

                return default;
            }

            if (_logger.IsDebug)
                _logger.Debug(
                    $"GetData<{typeof(T)}>({uri}) result: {await response.Content.ReadAsStringAsync(cancellationToken)}");

            T decoded =
                _jsonSerializer.Deserialize<T>(await response.Content.ReadAsStreamAsync(cancellationToken));

            return decoded;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Beacon API request exception({uri}): {e.Message}");
        }

        return default;
    }

    private class GetBlobSidecarsResponse
    {
        public required BlobSidecar[] Data { get; init; }
    }
}
