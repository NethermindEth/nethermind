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

using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Json;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.OApiClient
{
    public class BeaconNodeProxy : IBeaconNodeApi
    {
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonSerializerOptions;
        private readonly ILogger _logger;
        private const string JsonContentType = "application/json";

        public BeaconNodeProxy(ILogger<BeaconNodeProxy> logger,
            HttpClient httpClient)
        {
            _logger = logger;

            // Configure (and test) via IHttpClientFactory / AddHttpClient: https://docs.microsoft.com/en-us/dotnet/architecture/microservices/implement-resilient-applications/use-httpclientfactory-to-implement-resilient-http-requests

            _httpClient = httpClient;
            _jsonSerializerOptions = new JsonSerializerOptions();
            _jsonSerializerOptions.ConfigureNethermindCore2();
        }

        public async Task<ApiResponse<ulong>> GetGenesisTimeAsync(CancellationToken cancellationToken)
        {
            string uri = "node/genesis_time";

            using HttpResponseMessage httpResponse =
                await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            httpResponse.EnsureSuccessStatusCode(); // throws if not 200-299
            await using Stream contentStream = await httpResponse.Content.ReadAsStreamAsync();
            ulong content =
                await JsonSerializer.DeserializeAsync<ulong>(contentStream, _jsonSerializerOptions, cancellationToken);
            return ApiResponse.Create((StatusCode) (int) httpResponse.StatusCode, content);
        }

        public async Task<ApiResponse<Fork>> GetNodeForkAsync(CancellationToken cancellationToken)
        {
            string uri = "node/fork";

            using HttpResponseMessage httpResponse =
                await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            httpResponse.EnsureSuccessStatusCode(); // throws if not 200-299
            await using Stream contentStream = await httpResponse.Content.ReadAsStreamAsync();
            ForkInformation content =
                await JsonSerializer.DeserializeAsync<ForkInformation>(contentStream, _jsonSerializerOptions,
                    cancellationToken);
            return ApiResponse.Create((StatusCode) (int) httpResponse.StatusCode, content.Fork);
        }

        public async Task<ApiResponse<string>> GetNodeVersionAsync(CancellationToken cancellationToken)
        {
            string uri = "node/version";

            using HttpResponseMessage httpResponse =
                await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            // TODO: Return appropriate ApiResponse
            httpResponse.EnsureSuccessStatusCode(); // throws if not 200-299
            // if (httpResponse.Content.Headers.ContentType.MediaType != MediaTypeNames.Application.Json)
            await using Stream contentStream = await httpResponse.Content.ReadAsStreamAsync();
            string content =
                await JsonSerializer.DeserializeAsync<string>(contentStream, _jsonSerializerOptions, cancellationToken);
            return ApiResponse.Create((StatusCode) (int) httpResponse.StatusCode, content);
        }

        public async Task<ApiResponse<Syncing>> GetSyncingAsync(CancellationToken cancellationToken)
        {
            string uri = "node/syncing";

            using HttpResponseMessage httpResponse =
                await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            httpResponse.EnsureSuccessStatusCode(); // throws if not 200-299
            await using Stream contentStream = await httpResponse.Content.ReadAsStreamAsync();
            Syncing content =
                await JsonSerializer.DeserializeAsync<Syncing>(contentStream, _jsonSerializerOptions,
                    cancellationToken);
            return ApiResponse.Create((StatusCode) (int) httpResponse.StatusCode, content);
        }

        public async Task<ApiResponse<Attestation>> NewAttestationAsync(BlsPublicKey validatorPublicKey,
            bool proofOfCustodyBit, Slot slot, CommitteeIndex index,
            CancellationToken cancellationToken)
        {
            string baseUri = "validator/attestation";

            // NOTE: Spec 0.10.1 still has old Shard references in OAPI, although the spec has changed to Index;
            // use Index as it is easier to understand (i.e. the spec OAPI in 0.10.1 is wrong)

            Dictionary<string, string> queryParameters = new Dictionary<string, string>
            {
                ["validator_pubkey"] = validatorPublicKey.ToString(),
                ["poc_bit"] = proofOfCustodyBit ? "1" : "0",
                ["slot"] = slot.ToString(),
                ["index"] = index.ToString()
            };

            string uri = QueryHelpers.AddQueryString(baseUri, queryParameters);

            using HttpResponseMessage httpResponse =
                await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            int statusCode = (int) httpResponse.StatusCode;
            if (statusCode == (int) StatusCode.InvalidRequest
                || statusCode == (int) StatusCode.CurrentlySyncing)
            {
                return ApiResponse.Create<Attestation>((StatusCode) statusCode);
            }

            httpResponse.EnsureSuccessStatusCode(); // throws if not 200-299
            await using Stream contentStream = await httpResponse.Content.ReadAsStreamAsync();
            Attestation content =
                await JsonSerializer.DeserializeAsync<Attestation>(contentStream, _jsonSerializerOptions,
                    cancellationToken);
            return ApiResponse.Create((StatusCode) statusCode, content);
        }

        public async Task<ApiResponse<BeaconBlock>> NewBlockAsync(Slot slot, BlsSignature randaoReveal,
            CancellationToken cancellationToken)
        {
            string baseUri = "validator/block";

            Dictionary<string, string> queryParameters = new Dictionary<string, string>
            {
                ["slot"] = slot.ToString(),
                ["randao_reveal"] = randaoReveal.ToString()
            };

            string uri = QueryHelpers.AddQueryString(baseUri, queryParameters);

            using HttpResponseMessage httpResponse =
                await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            int statusCode = (int) httpResponse.StatusCode;
            if (statusCode == (int) StatusCode.InvalidRequest
                || statusCode == (int) StatusCode.CurrentlySyncing)
            {
                return ApiResponse.Create<BeaconBlock>((StatusCode) statusCode);
            }

            httpResponse.EnsureSuccessStatusCode(); // throws if not 200-299
            await using Stream contentStream = await httpResponse.Content.ReadAsStreamAsync();
            BeaconBlock content =
                await JsonSerializer.DeserializeAsync<BeaconBlock>(contentStream, _jsonSerializerOptions,
                    cancellationToken);
            return ApiResponse.Create((StatusCode) statusCode, content);
        }

        public async Task<ApiResponse> PublishAttestationAsync(Attestation signedAttestation,
            CancellationToken cancellationToken)
        {
            string uri = "validator/attestation";

            await using var memoryStream = new MemoryStream();
            await JsonSerializer.SerializeAsync(memoryStream, signedAttestation, _jsonSerializerOptions,
                cancellationToken);
            memoryStream.Position = 0;
            using var content = new StreamContent(memoryStream);
            content.Headers.ContentType = new MediaTypeHeaderValue(JsonContentType);
            using HttpResponseMessage httpResponse = await _httpClient.PostAsync(uri, content, cancellationToken);

            int statusCode = (int) httpResponse.StatusCode;
            if (statusCode == (int) StatusCode.InvalidRequest
                || statusCode == (int) StatusCode.CurrentlySyncing)
            {
                return new ApiResponse((StatusCode) statusCode);
            }

            httpResponse.EnsureSuccessStatusCode(); // throws if not 200-299

            return new ApiResponse((StatusCode) statusCode);
        }

        public async Task<ApiResponse> PublishBlockAsync(SignedBeaconBlock signedBlock,
            CancellationToken cancellationToken)
        {
            string uri = "validator/block";

            // TODO: .NET 5 will have JsonContent support, i.e. direct to stream
            //using HttpResponseMessage httpResponse = await _httpClient.PostAsJsonAsync(uri, signedBlock, cancellationToken);

            await using var memoryStream = new MemoryStream();
            await JsonSerializer.SerializeAsync(memoryStream, signedBlock, _jsonSerializerOptions, cancellationToken);
            memoryStream.Position = 0;
            using var content = new StreamContent(memoryStream);
            content.Headers.ContentType = new MediaTypeHeaderValue(JsonContentType);
            using HttpResponseMessage httpResponse = await _httpClient.PostAsync(uri, content, cancellationToken);

            int statusCode = (int) httpResponse.StatusCode;
            if (statusCode == (int) StatusCode.InvalidRequest
                || statusCode == (int) StatusCode.CurrentlySyncing)
            {
                return new ApiResponse((StatusCode) statusCode);
            }

            httpResponse.EnsureSuccessStatusCode(); // throws if not 200-299

            return new ApiResponse((StatusCode) statusCode);
        }

        public async Task<ApiResponse<IList<ValidatorDuty>>> ValidatorDutiesAsync(
            IList<BlsPublicKey> validatorPublicKeys, Epoch? epoch, CancellationToken cancellationToken)
        {
            string baseUri = "validator/duties";

            string uri = baseUri;
            foreach (var validatorPublicKey in validatorPublicKeys)
            {
                uri = QueryHelpers.AddQueryString(uri, "validator_pubkeys", validatorPublicKey.ToString());
            }

            if (epoch.HasValue)
            {
                uri = QueryHelpers.AddQueryString(uri, "epoch", epoch.Value.ToString());
            }

            using HttpResponseMessage httpResponse =
                await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            int statusCode = (int) httpResponse.StatusCode;
            if (statusCode == (int) StatusCode.InvalidRequest
                || statusCode == (int) StatusCode.DutiesNotAvailableForRequestedEpoch
                || statusCode == (int) StatusCode.CurrentlySyncing)
            {
                return ApiResponse.Create<IList<ValidatorDuty>>((StatusCode) (int) httpResponse.StatusCode);
            }

            httpResponse.EnsureSuccessStatusCode(); // throws if not 200-299
            await using Stream contentStream = await httpResponse.Content.ReadAsStreamAsync();
            IList<ValidatorDuty> content =
                await JsonSerializer.DeserializeAsync<IList<ValidatorDuty>>(contentStream, _jsonSerializerOptions,
                    cancellationToken);
            return ApiResponse.Create((StatusCode) (int) httpResponse.StatusCode, content);
        }
    }
}