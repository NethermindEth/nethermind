// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Json;
using EthProofValidator.src.Models;

namespace EthProofValidator.src.Clients
{
    public class EthProofsApiClient
    {
        private readonly HttpClient _httpClient;
        private const string BaseUrl = "https://ethproofs.org";

        public EthProofsApiClient()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 20
            };
            _httpClient = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        }

        public async Task<List<ClusterVerifier>?> GetActiveKeysAsync()
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<List<ClusterVerifier>>("/api/v0/verification-keys/active");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API Error] Failed to fetch active clusters: {ex.Message}");
                return null;
            }
        }

        public async Task<string?> GetVerificationKeyBinaryAsync(long proofId)
        {
            try
            {
                var vkBytes = await _httpClient.GetByteArrayAsync($"/api/verification-keys/download/{proofId}");
                return Convert.ToBase64String(vkBytes);
            }
            catch
            {
                return null;
            }
        }

        public async Task<List<ProofMetadata>?> GetProofsForBlockAsync(long blockId)
        {
            try
            {
                var results = await _httpClient.GetFromJsonAsync<ProofResponse>($"/api/blocks/{blockId}/proofs?page_size=20");
                return results?.Rows.Where(p => p.Status == "proved").ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API Error] Failed to fetch proofs for block {blockId}: {ex.Message}");
                return null;
            }
        }

        public async Task<byte[]?> DownloadProofAsync(long proofId)
        {
            try
            {
                return await _httpClient.GetByteArrayAsync($"/api/proofs/download/{proofId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API Error] Failed to download proof {proofId}: {ex.Message}");
                return null;
            }
        }
    }
}
