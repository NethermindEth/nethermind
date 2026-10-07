// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Follower;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Eez.Proving;

/// <summary>Reads the attestation rules the rollup manager applies to the next batch.</summary>
public interface IQuorumRegistrationReader
{
    /// <exception cref="ProveException">L1 did not serve the rules in time; the batch waits for the next slot.</exception>
    Task<QuorumRegistration> Read(IReadOnlyList<IAttester> attesters, CancellationToken token);
}

/// <summary>
/// Reads the rules for every batch, so a changed threshold, a newly registered proof system or a rotated signer applies
/// from the next batch without a restart. An attester whose proof system is not registered, or accepts another signer,
/// is not asked, since its proof would revert the batch. The manager's address is cached once read, so the node can
/// start before the rollup is registered.
/// </summary>
public sealed class QuorumRegistrationReader(IEezL1Api l1, Address registry, ulong rollupId, ILogManager logManager) : IQuorumRegistrationReader
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] RollupsSelector = Selector("rollups(uint64)");
    private static readonly byte[] ThresholdSelector = Selector("threshold()");
    private static readonly byte[] VerificationKeySelector = Selector("verificationKey(address)");
    private static readonly byte[] SignerSelector = Selector("signer()");

    private readonly ILogger _logger = logManager.GetClassLogger<QuorumRegistrationReader>();
    private Address? _manager;

    public async Task<QuorumRegistration> Read(IReadOnlyList<IAttester> attesters, CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            Address manager = _manager ??= await Manager(timeout.Token);
            Task<ValueHash256>[] keys = new Task<ValueHash256>[attesters.Count];
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i] = Key(manager, attesters[i], timeout.Token);
            }

            Task<byte[]> threshold = Word(manager, ThresholdSelector, timeout.Token);
            Task<ValueHash256[]> registered = Task.WhenAll(keys);
            await Task.WhenAll(threshold, registered);
            return new QuorumRegistration(Threshold(threshold.Result), registered.Result);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new ProveException(ProveFailureKind.Retryable, $"The rollup manager's attestation rules were not read within {ReadTimeout.TotalSeconds:F0} s.");
        }
    }

    private async Task<Address> Manager(CancellationToken token)
    {
        byte[] data = new byte[4 + 32];
        RollupsSelector.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(4 + 24), rollupId);
        byte[] result = await Read(registry, data, 3 * 32, token);
        Address manager = new(result.AsSpan(12, 20));
        return manager == Address.Zero
            ? throw new ProveException(ProveFailureKind.Retryable, $"Rollup {rollupId} has no manager in registry {registry} yet.")
            : manager;
    }

    private async Task<ValueHash256> Key(Address manager, IAttester attester, CancellationToken token)
    {
        byte[] data = new byte[4 + 32];
        VerificationKeySelector.CopyTo(data, 0);
        attester.ProofSystem.Bytes.CopyTo(data.AsSpan(4 + 12));
        ValueHash256 key = new(await Word(manager, data, token));
        if (key == default)
        {
            if (_logger.IsWarn) _logger.Warn($"Proof system {attester.ProofSystem} is not registered on rollup manager {manager}; its attester is not asked.");
            return default;
        }

        Address signer = new((await Word(attester.ProofSystem, SignerSelector, token)).AsSpan(12, 20));
        if (signer != attester.Signer)
        {
            if (_logger.IsWarn) _logger.Warn($"Proof system {attester.ProofSystem} accepts signer {signer}, not {attester.Signer}; its attester is not asked.");
            return default;
        }

        return key;
    }

    /// <summary>The manager accepts a batch once as many proofs verify; zero still takes one proof.</summary>
    private static int Threshold(byte[] word)
    {
        UInt256 threshold = new(word, isBigEndian: true);
        return threshold > AttestationQuorum.MaxAttesters ? int.MaxValue : Math.Max(1, (int)(ulong)threshold);
    }

    private Task<byte[]> Word(Address to, byte[] data, CancellationToken token) => Read(to, data, 32, token);

    private async Task<byte[]> Read(Address to, byte[] data, int size, CancellationToken token)
    {
        byte[]? result = await l1.Call(to, data, token);
        return result is not null && result.Length >= size
            ? result
            : throw new ProveException(ProveFailureKind.Retryable, $"L1 did not serve the view call to {to}.");
    }

    private static byte[] Selector(string signature) => Keccak.Compute(signature).Bytes[..4].ToArray();
}
