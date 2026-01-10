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

            int validCount = 0, totalCount = 0;
            foreach (var result in results)
            {
                if (result == ZkResult.Valid) validCount++;
                if (result != ZkResult.Failed && result != ZkResult.Skipped) totalCount++;
            }

            Console.WriteLine("   --------------------------------");
            Console.WriteLine(validCount * 2 >= totalCount
                ? $"✅ BLOCK #{blockId} ACCEPTED ({validCount}/{totalCount})"
                : $"❌ BLOCK #{blockId} REJECTED ({validCount}/{totalCount})");
        }

        private async Task<ZkResult> ProcessProofAsync(ProofMetadata proof, ZkProofVerifier? verifier)
        {
            if (verifier is null)
            {
                var zkType = proof.Cluster.ZkvmVersion.ZkVm.Type;
                this.DisplayProofResult(ZkResult.Skipped, proof.ProofId, zkType, $"No verifier for cluster {proof.ClusterId}");
                return ZkResult.Skipped;
            }

            var proofBytes = await _apiClient.DownloadProofAsync(proof.ProofId);
            if (proofBytes is null)
            {
                this.DisplayProofResult(ZkResult.Skipped, proof.ProofId, $"{verifier.ZkType}", "Could not download proof");
                return ZkResult.Skipped;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ZkResult result = verifier.Verify(proofBytes);
            sw.Stop();

            this.DisplayProofResult(result, proof.ProofId, $"{verifier.ZkType}", $"{sw.ElapsedMilliseconds} ms");
            return result;
        }

        private void DisplayProofResult(ZkResult result, long proofId, string zkType, string info)
        {
            var status = result switch
            {
                ZkResult.Valid => "✅ Valid",
                ZkResult.Invalid => "❌ Invalid",
                ZkResult.Failed => "⛔ Error",
                ZkResult.Skipped => "⚠️  Skipped",
                _ => "❓ Unknown"
            };

            Console.WriteLine($"   Proof {proofId} - {zkType, -15} : {status} ({info})");
        }
    }
}
