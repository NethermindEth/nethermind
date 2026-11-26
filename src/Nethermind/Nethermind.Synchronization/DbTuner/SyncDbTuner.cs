// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Attributes;
using Nethermind.Db;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.DbTuner;

public class SyncDbTuner
{
    private readonly ITunableDb? _blockDb;
    private readonly ITunableDb? _receiptDb;

    private readonly ITunableDb.TuneType _tuneType;
    private readonly ITunableDb.TuneType _blocksDbTuneType;

    [ConstructorWithSideEffect]
    public SyncDbTuner(
        ISyncConfig syncConfig,
        ISyncFeed<BodiesSyncBatch>? bodiesSyncFeed,
        ISyncFeed<ReceiptsSyncBatch>? receiptSyncFeed,
        [KeyFilter(DbNames.Blocks)] ITunableDb? blockDb,
        [KeyFilter(DbNames.Receipts)] ITunableDb? receiptDb
    )
    {
        if (syncConfig.TuneDbMode == ITunableDb.TuneType.Default && syncConfig.BlocksDbTuneDbMode == ITunableDb.TuneType.Default)
        {
            return;
        }

        if (bodiesSyncFeed is not null and not NoopSyncFeed<BodiesSyncBatch>)
        {
            bodiesSyncFeed.StateChanged += BodiesStateChanged;
        }

        if (receiptSyncFeed is not null and not NoopSyncFeed<ReceiptsSyncBatch>)
        {
            receiptSyncFeed.StateChanged += ReceiptsStateChanged;
        }

        _blockDb = blockDb;
        _receiptDb = receiptDb;

        _tuneType = syncConfig.TuneDbMode;
        _blocksDbTuneType = syncConfig.BlocksDbTuneDbMode;
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
