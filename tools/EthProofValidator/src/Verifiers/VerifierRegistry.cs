// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.EthProofValidator.Clients;
using Nethermind.EthProofValidator.Models;

namespace Nethermind.EthProofValidator.Verifiers;

public class VerifierRegistry(EthProofsApiClient apiClient): IDisposable
{
    private readonly EthProofsApiClient _apiClient = apiClient;
    private readonly ConcurrentDictionary<string, ZkProofVerifier> _verifiers = new();

    public async Task InitializeAsync()
    {
        var clusters = await _apiClient.GetActiveKeysAsync();

        if (clusters is null)
        {
            Console.WriteLine("No keys found.");
            return;
        }

        foreach (var cluster in clusters)
        {
            RegisterVerifier(cluster.Id, cluster.ZkType, cluster.VkBinary);
        }
        Console.WriteLine($"✅ Loaded {_verifiers.Count} verifiers.");
    }

    public ZkProofVerifier? GetVerifier(string clusterId)
    {
        _verifiers.TryGetValue(clusterId, out var verifier);
        return verifier;
    }

    public async Task<ZkProofVerifier?> TryAddVerifierAsync(ProofMetadata proof)
    {
        var type = proof.Cluster.ZkvmVersion.ZkVm.Type;
        var vkBinary = await _apiClient.GetVerificationKeyBinaryAsync(proof.ProofId);

        RegisterVerifier(proof.ClusterId, type, vkBinary);
        return GetVerifier(proof.ClusterId);
    }

    private void RegisterVerifier(string clusterId, string zkVm, string? vkBinary)
    {
        ZKType zkType = ZkTypeMapper.Parse(zkVm);
        if (zkType == ZKType.Unknown) return;

        if (string.IsNullOrEmpty(vkBinary) && !IsVerifiableWithoutVk(zkType)) return;

        _verifiers.AddOrUpdate(clusterId,
            _ => new ZkProofVerifier(zkType, vkBinary),
            (_, oldVerifier) =>
            {
                oldVerifier.Dispose();
                return new ZkProofVerifier(zkType, vkBinary);
            });
    }

    public void Dispose()
    {
        foreach (var verifier in _verifiers.Values)
        {
            verifier.Dispose();
        }
        _verifiers.Clear();
        GC.SuppressFinalize(this);
    }

    // Verifier(s) handles vk internally
    private static bool IsVerifiableWithoutVk(ZKType zkType) {
        return zkType == ZKType.Airbender;
    }
}
