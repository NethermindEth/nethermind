// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Db;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Synchronization.DbTuner;

public class SyncDbTuner
{
    private readonly ITunableDb? _stateDb;
    private readonly ITunableDb? _codeDb;
    private readonly ITunableDb? _blockDb;
    private readonly ITunableDb? _receiptDb;

    private readonly ITunableDb.TuneType _tuneType;
    private readonly ITunableDb.TuneType _blocksDbTuneType;

    public SyncDbTuner(
        ISyncConfig syncConfig,
        ISyncFeed<SnapSyncBatch>? snapSyncFeed,
        ISyncFeed<BodiesSyncBatch>? bodiesSyncFeed,
        ISyncFeed<ReceiptsSyncBatch>? receiptSyncFeed,
        [KeyFilter(DbNames.State)] ITunableDb? stateDb,
        [KeyFilter(DbNames.Code)] ITunableDb? codeDb,
        [KeyFilter(DbNames.Blocks)] ITunableDb? blockDb,
        [KeyFilter(DbNames.Receipts)] ITunableDb? receiptDb
    )
    {
        if (syncConfig.TuneDbMode == ITunableDb.TuneType.Default && syncConfig.BlocksDbTuneDbMode == ITunableDb.TuneType.Default)
        {
            // Do nothing.
            return;
        }

        // Only these three make sense as they are write heavy
        // Headers is used everywhere, so slowing read might slow the whole sync.
        // Statesync is read heavy, Forward sync is just plain too slow to saturate IO.
        if (snapSyncFeed is not NoopSyncFeed<SnapSyncBatch>)
        {
            snapSyncFeed.StateChanged += SnapStateChanged;
        }

        if (bodiesSyncFeed is not NoopSyncFeed<BodiesSyncBatch>)
        {
            bodiesSyncFeed.StateChanged += BodiesStateChanged;
        }

        if (receiptSyncFeed is not NoopSyncFeed<ReceiptsSyncBatch>)
        {
            receiptSyncFeed.StateChanged += ReceiptsStateChanged;
        }

        _stateDb = stateDb;
        _codeDb = codeDb;
        _blockDb = blockDb;
        _receiptDb = receiptDb;

        _tuneType = syncConfig.TuneDbMode;
        _blocksDbTuneType = syncConfig.BlocksDbTuneDbMode;
    }

    private void SnapStateChanged(object? sender, SyncFeedStateEventArgs e)
    {
        if (e.NewState == SyncFeedState.Active)
        {
            _stateDb?.Tune(_tuneType);
            _codeDb?.Tune(_tuneType);
        }
        else if (e.NewState == SyncFeedState.Finished)
        {
            _stateDb?.Tune(ITunableDb.TuneType.Default);
            _codeDb?.Tune(ITunableDb.TuneType.Default);
        }
    }

    private void BodiesStateChanged(object? sender, SyncFeedStateEventArgs e)
    {
        if (e.NewState == SyncFeedState.Active)
        {
            _blockDb?.Tune(_blocksDbTuneType);
        }
        else if (e.NewState == SyncFeedState.Finished)
        {
            _blockDb?.Tune(ITunableDb.TuneType.Default);
        }
    }

    private void ReceiptsStateChanged(object? sender, SyncFeedStateEventArgs e)
    {
        if (e.NewState == SyncFeedState.Active)
        {
            _receiptDb?.Tune(_tuneType);
        }
        else if (e.NewState == SyncFeedState.Finished)
        {
            _receiptDb?.Tune(ITunableDb.TuneType.Default);
        }
    }
}
