// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Posting;
using Nethermind.Logging;

namespace Nethermind.Eez.Proving;

/// <summary>The rollup manager's attestation rules for one batch: how many proofs it takes, and each attester's key.</summary>
/// <param name="VerificationKeys">By attester, the key its proof system is registered with; zero when it is not asked.</param>
public sealed record QuorumRegistration(int Threshold, ValueHash256[] VerificationKeys)
{
    public int Registered => VerificationKeys.Count(static k => k != default);
}

/// <summary>
/// Collects the proofs a batch needs. Every registered attester is asked in parallel with the batch naming only its
/// own proof system, and each proof is checked before it counts: 65 bytes, a recovery byte of 27 or 28, a low s, and
/// the attester's key over the public inputs hash computed here, since one invalid proof reverts the whole batch on
/// L1. Once the threshold is met, proofs arriving within <paramref name="grace"/> are kept too, so the same fastest
/// attesters are not the only ones ever recorded; the rest are cancelled.
/// </summary>
public sealed class AttestationQuorum(IReadOnlyList<IAttester> attesters, ProveRetry retry, TimeSpan grace, ILogManager logManager)
{
    public const int MaxAttesters = 16;

    private const int ProofSize = 65;

    private static readonly byte[] HalfCurveOrder = Convert.FromHexString("7fffffffffffffffffffffffffffffff5d576e7357a4501ddfe92f46681b20a0");

    private readonly IAttester[] _attesters = [.. attesters.OrderBy(static a => a.ProofSystem)];
    private readonly EthereumEcdsa _ecdsa = new(0);
    private readonly ILogger _logger = logManager.GetClassLogger<AttestationQuorum>();

    /// <summary>The proof systems of the configured attesters, ascending, which is the order a batch lists them in.</summary>
    public Address[] ProofSystems => [.. _attesters.Select(static a => a.ProofSystem)];

    public IReadOnlyList<IAttester> Attesters => _attesters;

    /// <returns>The proofs, ascending by proof system, at least as many as the threshold.</returns>
    /// <exception cref="ProveException">The quorum was not reached.</exception>
    public async Task<Attestation[]> Attest(ProveRequest request, QuorumRegistration registration, BundleTarget target, CancellationToken token)
    {
        if (registration.Threshold > registration.Registered)
        {
            throw new ProveException(ProveFailureKind.Backend,
                $"The threshold of {registration.Threshold} is not reachable: {registration.Registered} of {_attesters.Length} attesters are registered.");
        }

        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        List<Task<Attestation>> pending = [];
        for (int i = 0; i < _attesters.Length; i++)
        {
            if (registration.VerificationKeys[i] != default)
            {
                pending.Add(ProveOne(_attesters[i], registration.VerificationKeys[i], request, target, cancel.Token));
            }
        }

        Tally tally = new(registration.Registered, registration.Threshold);
        Task? graceOver = null;
        while (pending.Count > 0)
        {
            Task finished = graceOver is null ? await Task.WhenAny(pending) : await Task.WhenAny([.. pending, graceOver]);
            if (finished == graceOver)
            {
                break;
            }

            Task<Attestation> done = (Task<Attestation>)finished;
            pending.Remove(done);
            try
            {
                tally.Attestations.Add(await done);
            }
            catch (ProveException e)
            {
                if (_logger.IsWarn) _logger.Warn($"An attester did not prove blocks {request.FromBlock}-{request.ToBlock}: {e.Message}");
                tally.Failures.Add(e);
            }

            if (tally.Attestations.Count >= registration.Threshold)
            {
                graceOver ??= Task.Delay(grace, token);
            }
            else if (tally.Verdict(pending.Count) is { } verdict)
            {
                await Abandon(pending, cancel);
                throw verdict;
            }
        }

        await Abandon(pending, cancel);
        if (tally.Attestations.Count < registration.Threshold)
        {
            throw tally.Verdict(0) ?? new ProveException(ProveFailureKind.Backend, "The attestation quorum was not reached.");
        }

        return [.. tally.Attestations.OrderBy(static a => a.ProofSystem)];
    }

    /// <summary>Cancels the attesters still proving and waits for them, so none outlives the batch.</summary>
    private static async Task Abandon(List<Task<Attestation>> pending, CancellationTokenSource cancel)
    {
        await cancel.CancelAsync();
        foreach (Task<Attestation> task in pending)
        {
            try
            {
                await task;
            }
            catch (ProveException)
            {
            }
        }
    }

    private async Task<Attestation> ProveOne(IAttester attester, ValueHash256 verificationKey, ProveRequest request, BundleTarget target, CancellationToken token)
    {
        ProveRequest own = request.For(attester.ProofSystem);
        ValueHash256 expected = PostBatchProfile.PublicInputsHash(own.Batch, request.RollupId, verificationKey);
        byte[] proof;
        try
        {
            proof = await retry.Prove(attester, own, target, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new ProveException(ProveFailureKind.Retryable, $"Attester {attester.Signer} was cancelled.");
        }

        return Validity(proof, expected, attester.Signer) is { } invalid
            ? throw new ProveException(ProveFailureKind.Backend, $"Attester {attester.Signer} returned an invalid proof: {invalid}.")
            : new Attestation(attester.ProofSystem, proof);
    }

    /// <returns>Why the proof would not verify on L1, or <see langword="null"/> when it would.</returns>
    internal string? Validity(byte[] proof, in ValueHash256 digest, Address signer)
    {
        if (proof.Length != ProofSize)
        {
            return $"{proof.Length} bytes, not {ProofSize}";
        }

        byte v = proof[ProofSize - 1];
        if (v is not (27 or 28))
        {
            return $"the recovery byte {v}, not 27 or 28";
        }

        if (proof.AsSpan(32, 32).SequenceCompareTo(HalfCurveOrder) > 0)
        {
            return "a high s";
        }

        Address? recovered = _ecdsa.RecoverAddress(new Signature(proof.AsSpan(0, 64), v - 27), in digest);
        return recovered == signer ? null : $"it recovers to {recovered}, not {signer}, over {digest}";
    }

    /// <summary>The proofs and failures so far, and whether the quorum can still be reached.</summary>
    private sealed class Tally(int registered, int threshold)
    {
        public List<Attestation> Attestations { get; } = new(registered);

        public List<ProveException> Failures { get; } = [];

        /// <summary>
        /// The quorum's failure once it cannot be reached with <paramref name="pending"/> attesters left, or
        /// <see langword="null"/> while it still can. An effect is only refused when as many attesters refuse it as it
        /// takes to block the quorum: fewer cannot outvote the rest.
        /// </summary>
        public ProveException? Verdict(int pending)
        {
            if (Attestations.Count + pending >= threshold)
            {
                return null;
            }

            int blocking = registered - threshold + 1;
            (RefusedEffect? effect, int refusals) = MostRefused();
            if (effect is not null && refusals >= blocking)
            {
                return new ProveException(ProveFailureKind.Actionable, $"{refusals} of {registered} attesters refused the same effect, enough to block the quorum.", effect);
            }

            if (refusals + pending >= blocking)
            {
                return null;
            }

            string summary = string.Join("; ", Failures.Select(static f => f.Message));
            string message = $"{Attestations.Count} of {registered} attesters proved the window, {threshold} required: {summary}";
            return Failures.All(static f => f.Kind == ProveFailureKind.Retryable)
                ? new ProveException(ProveFailureKind.Retryable, message)
                : new ProveException(ProveFailureKind.Backend, message);
        }

        private (RefusedEffect? Effect, int Refusals) MostRefused()
        {
            RefusedEffect? most = null;
            int mostRefusals = 0;
            foreach (ProveException failure in Failures)
            {
                if (failure.Refused is not { } effect)
                {
                    continue;
                }

                int refusals = Failures.Count(f => f.Refused == effect);
                if (refusals > mostRefusals)
                {
                    (most, mostRefusals) = (effect, refusals);
                }
            }

            return (most, mostRefusals);
        }
    }
}
