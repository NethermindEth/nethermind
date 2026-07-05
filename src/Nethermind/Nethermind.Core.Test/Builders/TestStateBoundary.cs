// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.Core.Test.Builders;

/// <summary>
/// Settable <see cref="IStateBoundary"/> stand-in. Share one instance across
/// <see cref="BlockTreeBuilder"/>s (via <see cref="BlockTreeBuilder.WithDatabaseFrom"/>) to
/// simulate the state backend surviving a block tree restart.
/// </summary>
public class TestStateBoundary : IStateBoundary
{
    public ulong? OldestStateBlock { get; set; }
    public ulong? RetentionWindowBlocks { get; set; }
    public ulong? BestPersistedState { get; set; }
    public Hash256? BestPersistedStateRoot { get; set; }

    public bool TryGetBestPersistedState(out ulong blockNumber, [NotNullWhen(true)] out Hash256? stateRoot)
    {
        blockNumber = BestPersistedState ?? 0;
        stateRoot = BestPersistedStateRoot;
        return BestPersistedState.HasValue && BestPersistedStateRoot is not null;
    }
}
