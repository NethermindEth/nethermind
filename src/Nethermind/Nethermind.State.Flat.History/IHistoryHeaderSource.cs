// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History;

/// <summary>The verifier's window onto this node's own headers, so it can compare rebuilt state against what the
/// node itself committed to rather than against a foreign source.</summary>
public interface IHistoryHeaderSource
{
    /// <summary>The state root this node's header records for <paramref name="block"/>, or <c>null</c> when no
    /// header for that number is available - which the walk reports as a mismatch rather than skipping.</summary>
    ValueHash256? TryGetStateRoot(ulong block);
}
