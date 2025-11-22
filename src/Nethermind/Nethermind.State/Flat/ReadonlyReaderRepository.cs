// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat;

public class ReadonlyReaderRepository
{
    private ConcurrentDictionary<StateId, RefCountingDisposableBox<SnapshotBundle>> _sharedReader = new();
    private readonly IFlatDiffRepository _flatDiffRepository;

    public ReadonlyReaderRepository(IFlatDiffRepository flatDiffRepository)
    {
        flatDiffRepository.ReorgBoundaryReached += (sender, reached) => ClearAllReader();
        _flatDiffRepository = flatDiffRepository;
    }

    private void ClearAllReader()
    {
        // Readers take up a persistence snapshot which which eventually slow down the db. So we need to clear them
        // on persist, that way a new snapshot will be used later.
        using ArrayPoolListRef<StateId> toRemoves = new ArrayPoolListRef<StateId>(_sharedReader.Count, _sharedReader.Keys);

        foreach (var stateId in toRemoves)
        {
            if (_sharedReader.TryRemove(stateId, out RefCountingDisposableBox<SnapshotBundle> snapshotBundle))
            {
                snapshotBundle.Dispose();
            }
        }
    }

    public RefCountingDisposableBox<SnapshotBundle>? GatherReadOnlyReaderAtBaseBlock(StateId baseBlock)
    {
        if (_sharedReader.TryGetValue(baseBlock, out var snapshotBundle) && snapshotBundle.TryAcquire())
        {
            return snapshotBundle;
        }

        SnapshotBundle? bundle = _flatDiffRepository.GatherReaderAtBaseBlock(baseBlock, isReadOnly: true);
        if (bundle is null) return null;

        RefCountingDisposableBox<SnapshotBundle> newReader = new RefCountingDisposableBox<SnapshotBundle>(bundle);
        newReader.AcquireLease();
        if (!_sharedReader.TryAdd(baseBlock, newReader))
        {
            newReader.Dispose();
        }

        return newReader;
    }

}
