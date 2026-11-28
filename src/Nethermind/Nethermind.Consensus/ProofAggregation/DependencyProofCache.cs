// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Metrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Serializes EIP-8288 proving, remembers verified proofs and the statements a builder has proven, and proves
/// production statements off the payload deadline.</summary>
public sealed class DependencyProofCache(ILeanProofVerifier verifier, ILogManager logManager) : ILeanProofVerifier
{
    private const int MaxVerified = 256;
    private const int MaxStatements = 16;
    private const long MaxStatementBytes = 32 * 1024 * 1024;

    private readonly ILogger _logger = logManager.GetClassLogger<DependencyProofCache>();
    private readonly Lock _cacheLock = new();
    private readonly Lock _provingLock = new();
    private readonly Dictionary<ValueHash256, LinkedListNode<ValueHash256>> _verified = [];
    private readonly LinkedList<ValueHash256> _verifiedOrder = [];
    private readonly LinkedList<Statement> _statements = [];
    private long _statementBytes;
    private Task? _scheduled;
    private PendingStatement? _pending;

    /// <summary>A proven statement: the dependencies a proof covers.</summary>
    public sealed record Statement(IReadOnlyList<AggregatedDependency> Deps, ValueHash256 DepsHash, byte[] Proof);

    private sealed record PendingStatement(IReadOnlyList<AggregatedDependency> Deps, ValueHash256 DepsHash, AggregationInput Input);

    /// <inheritdoc/>
    public void EnsureAvailable() => verifier.EnsureAvailable();

    /// <inheritdoc/>
    public bool VerifyLeanSphincs(in ValueHash256 message, in ValueHash256 publicKey, ReadOnlySpan<byte> witness) =>
        verifier.VerifyLeanSphincs(in message, in publicKey, witness);

    /// <inheritdoc/>
    /// <remarks>A proof that verified once is accepted again without the native call: a produced block is verified when
    /// proven and again on import.</remarks>
    public bool VerifyDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
    {
        ValueHash256 key = VerifiedKey(in depsHash, aggregatedVk, proof);
        lock (_cacheLock)
        {
            if (_verified.TryGetValue(key, out LinkedListNode<ValueHash256>? node))
            {
                _verifiedOrder.Remove(node);
                _verifiedOrder.AddLast(node);
                Interlocked.Increment(ref Metrics.LeanVerificationCacheHits);
                return true;
            }
        }

        if (!verifier.VerifyDependencyProof(in depsHash, aggregatedVk, proof)) return false;
        RememberVerified(key);
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Runs one native proof at a time and verifies it before returning it.</remarks>
    public byte[] ProveDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
    {
        lock (_provingLock)
        {
            long started = Stopwatch.GetTimestamp();
            byte[] proof = verifier.ProveDependencyProof(in depsHash, aggregatedVk, input);
            long elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Interlocked.Increment(ref Metrics.LeanNativeProofs);
            Interlocked.Add(ref Metrics.LeanNativeProveMilliseconds, elapsed);
            Metrics.LeanNativeProveLastMilliseconds = elapsed;
            if (proof.Length is 0 or > Eip8288Constants.MaxAggregateProofBytes || !verifier.VerifyDependencyProof(in depsHash, aggregatedVk, proof))
                throw new InvalidOperationException("Produced EIP-8288 proof failed verification.");

            RememberVerified(VerifiedKey(in depsHash, aggregatedVk, proof));
            if (_logger.IsDebug)
                _logger.Debug($"EIP-8288 proof in {elapsed} ms, {proof.Length} bytes: {input.Deps.Count} direct, {input.RecursiveProofs.Count} recursive, {input.Discards.Count} discarded");
            return proof;
        }
    }

    /// <summary>The proof of exactly <paramref name="depsHash"/>, if proven.</summary>
    public bool TryGetProof(in ValueHash256 depsHash, out byte[] proof)
    {
        lock (_cacheLock)
        {
            for (LinkedListNode<Statement>? node = _statements.Last; node is not null; node = node.Previous)
            {
                if (node.Value.DepsHash != depsHash) continue;
                proof = node.Value.Proof;
                return true;
            }
        }

        proof = [];
        return false;
    }

    /// <summary>The proven statement sharing the most dependencies with <paramref name="needed"/>.</summary>
    public Statement? BestOverlap(IReadOnlySet<AggregatedDependency> needed)
    {
        Statement? best = null;
        int bestShared = 0;
        lock (_cacheLock)
        {
            foreach (Statement statement in _statements)
            {
                int shared = 0;
                foreach (AggregatedDependency dependency in statement.Deps)
                {
                    if (needed.Contains(dependency)) shared++;
                }

                if (shared > bestShared) (best, bestShared) = (statement, shared);
            }
        }

        return best;
    }

    /// <summary>Records a proven statement for reuse.</summary>
    public void Remember(IReadOnlyList<AggregatedDependency> deps, in ValueHash256 depsHash, byte[] proof)
    {
        Statement statement = new(deps, depsHash, proof);
        lock (_cacheLock)
        {
            while (_statements.Count > 0 && (_statements.Count >= MaxStatements || _statementBytes + proof.Length > MaxStatementBytes))
            {
                _statementBytes -= _statements.First!.Value.Proof.Length;
                _statements.RemoveFirst();
            }

            _statements.AddLast(statement);
            _statementBytes += proof.Length;
        }
    }

    /// <summary>Raised when a scheduled statement has been proven and remembered.</summary>
    public event Action? ScheduledProofCompleted;

    /// <summary>Whether a scheduled statement is still being proven.</summary>
    public bool IsScheduled => Volatile.Read(ref _scheduled) is { IsCompleted: false };

    /// <summary>Proves a production statement in the background and remembers it for the next build of the same block.</summary>
    /// <remarks>At most one statement runs at a time. A running statement is never cancelled: one that takes longer than a
    /// slot would otherwise never finish while the pending set keeps changing. A statement requested meanwhile waits as the
    /// single pending one, replacing any older pending request.</remarks>
    public void Schedule(IReadOnlyList<AggregatedDependency> deps, in ValueHash256 depsHash, AggregationInput input)
    {
        PendingStatement statement = new(deps, depsHash, input);
        lock (_cacheLock)
        {
            if (_scheduled is { IsCompleted: false }) _pending = statement;
            else Start(statement);
        }
    }

    private void Start(PendingStatement statement)
    {
        using (ExecutionContext.SuppressFlow())
            _scheduled = Task.Factory.StartNew(() => Run(statement), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Run(PendingStatement statement)
    {
        try
        {
            if (!TryGetProof(statement.DepsHash, out _))
            {
                byte[] proof = DependencyProofAggregator.Prove(statement.Input, this, statement.DepsHash);
                Remember(statement.Deps, statement.DepsHash, proof);
                ScheduledProofCompleted?.Invoke();
            }
        }
        catch (LeanProverBusyException)
        {
            if (_logger.IsDebug) _logger.Debug("Scheduled EIP-8288 production proof skipped: the prover is busy");
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn) _logger.Warn($"Scheduled EIP-8288 production proof failed: {exception.Message}");
        }
        finally
        {
            lock (_cacheLock)
            {
                if (_pending is { } next)
                {
                    _pending = null;
                    Start(next);
                }
            }
        }
    }

    private void RememberVerified(in ValueHash256 key)
    {
        lock (_cacheLock)
        {
            if (_verified.ContainsKey(key)) return;
            if (_verified.Count >= MaxVerified)
            {
                _verified.Remove(_verifiedOrder.First!.Value);
                _verifiedOrder.RemoveFirst();
            }

            _verified.Add(key, _verifiedOrder.AddLast(key));
        }
    }

    private static ValueHash256 VerifiedKey(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
    {
        KeccakHash hash = KeccakHash.Create();
        hash.Update(depsHash.Bytes);
        hash.Update(aggregatedVk);
        hash.Update(proof);
        return hash.GenerateValueHash();
    }
}
