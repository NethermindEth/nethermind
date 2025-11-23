// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

public interface IFlatDiffRepository
{
    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;
    SnapshotBundle? GatherReaderAtBaseBlock(StateId baseBlock);
    void AddSnapshot(StateId startingBlock, StateId endBlock, Snapshot snapshot);
    void FlushCache(CancellationToken cancellationToken);
    bool HasStateForBlock(StateId stateId);
}
