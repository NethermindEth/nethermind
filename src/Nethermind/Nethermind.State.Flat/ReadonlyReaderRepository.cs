// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat;

public class ReadonlyReaderRepository: IAsyncDisposable
{
    private ConcurrentDictionary<StateId, RefCountingDisposableBox<ReadOnlySnapshotBundle>> _sharedReader = new();
    private readonly IFlatDiffRepository _flatDiffRepository;
    private readonly Task _clearReaderTask;

    public ReadonlyReaderRepository(IFlatDiffRepository flatDiffRepository, IProcessExitSource exitSource)
    {
        flatDiffRepository.ReorgBoundaryReached += (sender, reached) => ClearAllReader();
        _flatDiffRepository = flatDiffRepository;

        _clearReaderTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            CancellationToken cancellation = exitSource.Token;

            try
            {
                while (true)
                {
                    await timer.WaitForNextTickAsync(cancellation);

                    ClearAllReader();
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void ClearAllReader()
    {
        // Readers take up a persistence snapshot which which eventually slow down the db. So we need to clear them
        // on persist, that way a new snapshot will be used later.
        using ArrayPoolListRef<StateId> toRemoves = new ArrayPoolListRef<StateId>(_sharedReader.Count, _sharedReader.Keys);

        foreach (var stateId in toRemoves)
        {
            if (_sharedReader.TryRemove(stateId, out RefCountingDisposableBox<ReadOnlySnapshotBundle>? snapshotBundle))
            {
                snapshotBundle.Dispose();
            }
        }
    }

    public RefCountingDisposableBox<ReadOnlySnapshotBundle>? GatherReadOnlyReaderAtBaseBlock(StateId baseBlock)
    {
        if (_sharedReader.TryGetValue(baseBlock, out var snapshotBundle))
        {
            if (snapshotBundle.TryAcquire())
            {
                return snapshotBundle;
            }
            else
            {
                _sharedReader.TryRemove(baseBlock, out _);
            }
        }

        ReadOnlySnapshotBundle? bundle = _flatDiffRepository.GatherReadOnlyReaderAtBaseBlock(baseBlock);
        if (bundle is null) return null;

        RefCountingDisposableBox<ReadOnlySnapshotBundle> newReader = new RefCountingDisposableBox<ReadOnlySnapshotBundle>(bundle);
        newReader.AcquireLease();
        if (!_sharedReader.TryAdd(baseBlock, newReader))
        {
            newReader.Dispose();
        }

        return newReader;
    }

    public async ValueTask DisposeAsync()
    {
        await _clearReaderTask;
    }
}
