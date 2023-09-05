// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    private readonly ITunableDb? _receiptBlocksDb;
    private readonly ITunableDb? _receiptTxIndexDb;

    private ITunableDb.TuneType _tuneType;
    private ITunableDb.TuneType _blocksDbTuneType;
    private ITunableDb.TuneType _receiptsBlocksDbTuneType;

    public SyncDbTuner(
        ISyncConfig syncConfig,
        ISyncFeed<SnapSyncBatch>? snapSyncFeed,
        ISyncFeed<BodiesSyncBatch>? bodiesSyncFeed,
        ISyncFeed<ReceiptsSyncBatch>? receiptSyncFeed,
        ITunableDb? stateDb,
        ITunableDb? codeDb,
        ITunableDb? blockDb,
        ITunableDb? receiptBlocksDb,
        ITunableDb? receiptTxIndexDb
    )
    {
        // Only these three make sense as they are write heavy
        // Headers is used everywhere, so slowing read might slow the whole sync.
        // Statesync is read heavy, Forward sync is just plain too slow to saturate IO.
        if (snapSyncFeed != null)
        {
            snapSyncFeed.StateChanged += SnapStateChanged;
        }

        if (bodiesSyncFeed != null)
        {
            bodiesSyncFeed.StateChanged += BodiesStateChanged;
        }

        if (receiptSyncFeed != null)
        {
            receiptSyncFeed.StateChanged += ReceiptsStateChanged;
        }

        _stateDb = stateDb;
        _codeDb = codeDb;
        _blockDb = blockDb;
        _receiptBlocksDb = receiptBlocksDb;
        _receiptTxIndexDb = receiptTxIndexDb;

        _tuneType = syncConfig.TuneDbMode;
        _blocksDbTuneType = syncConfig.BlocksDbTuneDbMode;
        _receiptsBlocksDbTuneType = syncConfig.ReceiptsDbTuneDbMode;
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
            _receiptBlocksDb?.Tune(_receiptsBlocksDbTuneType);
            _receiptTxIndexDb?.Tune(_tuneType);
        }
        else if (e.NewState == SyncFeedState.Finished)
        {
            _receiptBlocksDb?.Tune(ITunableDb.TuneType.Default);
            _receiptTxIndexDb?.Tune(ITunableDb.TuneType.Default);
        }
    }
}
