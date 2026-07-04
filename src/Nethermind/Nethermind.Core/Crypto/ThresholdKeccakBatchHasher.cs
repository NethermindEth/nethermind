// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.Core.Crypto;

/// <summary>
/// Routes a keccak batch to a fast backend (e.g. GPU) once it reaches a minimum size, otherwise to a CPU fallback.
/// </summary>
/// <remarks>
/// Size-based routing: batches of at least <c>minBatch</c> messages go to the fast backend, smaller batches to the CPU
/// backend, whose per-message overhead is lower than an offload dispatch. Failure containment: the first exception from
/// the fast backend is logged once and permanently disables it for the process lifetime - a flaky accelerator must never
/// repeatedly disrupt hashing, so every subsequent batch (of any size) is served by the CPU backend. The failing batch
/// itself is transparently re-hashed on the CPU backend, so a fault is never observable to the caller as a wrong result
/// or a thrown exception. Thread-safe: the disabled flag is a single volatile write; both backends must be thread-safe
/// for concurrent <see cref="HashBatch"/> calls to be safe. Owns its backends: disposing the router disposes any
/// disposable backend (the GPU backend holds native accelerator resources).
/// </remarks>
public sealed class ThresholdKeccakBatchHasher : IKeccakBatchHasher, IDisposable
{
    private readonly IKeccakBatchHasher _fastBackend;
    private readonly IKeccakBatchHasher _cpuFallback;
    private readonly int _minBatch;
    private readonly ILogger _logger;

    // 0 = fast backend live; 1 = permanently disabled after a fault. Volatile so a fault on one thread is seen by all.
    private int _fastDisabled;

    /// <summary>Creates a router over a fast backend and a CPU fallback.</summary>
    /// <param name="fastBackend">Preferred backend for large batches (e.g. GPU); may be flaky.</param>
    /// <param name="cpuFallback">Always-available CPU backend used for small batches and after any fast-backend fault.</param>
    /// <param name="minBatch">Inclusive minimum message count routed to <paramref name="fastBackend"/>; below it the CPU backend is used.</param>
    /// <param name="logManager">Source of the logger used to warn once when the fast backend is disabled.</param>
    /// <exception cref="ArgumentNullException">A backend or the log manager is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minBatch"/> is negative.</exception>
    public ThresholdKeccakBatchHasher(IKeccakBatchHasher fastBackend, IKeccakBatchHasher cpuFallback, int minBatch, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(fastBackend);
        ArgumentNullException.ThrowIfNull(cpuFallback);
        ArgumentNullException.ThrowIfNull(logManager);
        ArgumentOutOfRangeException.ThrowIfNegative(minBatch);

        _fastBackend = fastBackend;
        _cpuFallback = cpuFallback;
        _minBatch = minBatch;
        _logger = logManager.GetClassLogger<ThresholdKeccakBatchHasher>();
    }

    /// <inheritdoc/>
    public void HashBatch(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs)
    {
        // Validate the caller contract up front so a malformed-batch ArgumentException throws to the caller and is never
        // mistaken for a backend fault - only genuine backend failures may retire the fast backend.
        ValidateContract(flat, offsets, outputs);

        if (offsets.Length >= _minBatch && Volatile.Read(ref _fastDisabled) == 0)
        {
            try
            {
                _fastBackend.HashBatch(flat, offsets, outputs);
                return;
            }
            catch (Exception e)
            {
                // First fault permanently retires the fast backend; the batch falls through to the CPU backend below.
                if (Interlocked.Exchange(ref _fastDisabled, 1) == 0 && _logger.IsWarn)
                {
                    _logger.Warn($"Fast keccak batch backend failed and is now disabled for the process lifetime: {e.Message}");
                }
            }
        }

        _cpuFallback.HashBatch(flat, offsets, outputs);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        (_fastBackend as IDisposable)?.Dispose();
        (_cpuFallback as IDisposable)?.Dispose();
    }

    // Same contract the batch backends enforce: equal output length, last offset == flat length, monotonic in-bounds offsets.
    private static void ValidateContract(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs)
    {
        if (offsets.Length != outputs.Length) ThrowLengthMismatch();
        if (offsets.Length > 0 && offsets[^1] != flat.Length) ThrowLastOffsetMismatch();

        int prev = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            int end = offsets[i];
            if (end < prev || end > flat.Length) ThrowInvalidOffsets();
            prev = end;
        }
    }

    private static void ThrowLengthMismatch() =>
        throw new ArgumentException("offsets and outputs must have equal length.");

    private static void ThrowLastOffsetMismatch() =>
        throw new ArgumentException("Last offset must equal the flat input length.");

    private static void ThrowInvalidOffsets() =>
        throw new ArgumentException("offsets must be non-decreasing and within the flat input bounds.");
}
