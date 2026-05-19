// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Synchronization.FastSync;

/// <summary>
/// Replacement for post-snap state healing. Implementations reconstruct any missing trie spine
/// locally from the leaves snap sync committed and, in the long run, apply buffered EIP-7928
/// Block Access Lists to catch up from the snap pivot to head.
/// </summary>
/// <remarks>
/// Today only the trie-reassembly half is implemented; BAL replay is a follow-up. Callers should
/// invoke <see cref="Run"/> immediately after snap sync finishes and fall through to the legacy
/// healing path (<c>StateSyncRunner.RunStateSyncRounds</c>) when it returns <see langword="false"/>.
/// A storage backend that cannot support the algorithm (e.g., the hash-keyed Patricia store)
/// returns a no-op implementation, so callers can wire this in unconditionally.
/// </remarks>
public interface IBalHealing
{
    /// <summary>
    /// Run the BAL-based healing flow against the current snap pivot.
    /// </summary>
    /// <returns><see langword="true"/> when sync is now complete at the pivot and the caller may
    /// skip traditional healing; <see langword="false"/> on any unsupported config, mismatch, or
    /// failure so the caller falls back to <c>RunStateSyncRounds</c>.</returns>
    Task<bool> Run(CancellationToken token);
}

/// <summary>
/// No-op implementation returned when the storage backend cannot support BAL healing
/// (e.g., the legacy hash-keyed Patricia store). Always returns <see langword="false"/> so the
/// caller falls through to the existing healing path.
/// </summary>
public sealed class NoopBalHealing : IBalHealing
{
    public static readonly NoopBalHealing Instance = new();
    private NoopBalHealing() { }

    public Task<bool> Run(CancellationToken token) => Task.FromResult(false);
}
