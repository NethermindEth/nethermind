// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Json;
using EthProofValidator.src.Clients;
using EthProofValidator.src.Models;
using EthProofValidator.src.Verifiers;

namespace EthProofValidator.src
{
    public class BlockValidator
    {
        private readonly EthProofsApiClient _apiClient;
        private readonly VerifierRegistry _registry;

        public BlockValidator()
        {
            _apiClient = new EthProofsApiClient();
            _registry = new VerifierRegistry(_apiClient);
        }

        public async Task InitializeAsync() => await _registry.InitializeAsync();

        public async Task ValidateBlockAsync(long blockId)
        {
            Console.WriteLine($"\n📦 Processing Block #{blockId}");
            
            var proofs = await _apiClient.GetProofsForBlockAsync(blockId);
            if (proofs == null || proofs.Count == 0)
            {
                Console.WriteLine("No proofs found.");
                return;
            }

            var tasks = proofs.Select(async proof =>
            {
                var verifier = _registry.GetVerifier(proof.ClusterId) ?? await _registry.TryAddVerifierAsync(proof);
                return await ProcessProofAsync(proof, verifier);
            });
            var results = await Task.WhenAll(tasks);

            int validCount = results.Count(r => r == 1);
            int totalCount = results.Count(r => r != -1); // Exclude skipped proofs

            Console.WriteLine("   -----------------------------");
            Console.WriteLine((double)validCount / totalCount >= 0.5
                ? $"✅ BLOCK #{blockId} ACCEPTED ({validCount}/{totalCount})"
                : $"❌ BLOCK #{blockId} REJECTED ({validCount}/{totalCount})");
        }

        private async Task<int> ProcessProofAsync(ProofMetadata proof, ZkProofVerifier? verifier)
        {
            if (verifier == null)
            {
                var zkType = proof.Cluster.ZkvmVersion.ZkVm.Type;
                Console.WriteLine($"   ⚠️  Skipping proof {proof.ProofId}: No verifier for cluster ({zkType}) {proof.ClusterId}");
                return -1;
            }

            var proofBytes = await _apiClient.DownloadProofAsync(proof.ProofId);
            if (proofBytes == null) return -1;

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool isValid = verifier.Verify(proofBytes);
                sw.Stop();
                
                Console.WriteLine($"   Proof {proof.ProofId,-10} : {(isValid ? "✅ Valid" : "❌ Invalid")} ({verifier.ZkType}, {sw.ElapsedMilliseconds} ms)");
                return isValid ? 1 : 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Error processing proof {proof.ProofId}: {ex.Message}");
                return 0;
            }
        }
    }
}
