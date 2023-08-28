// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Db;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.VerkleSync;

namespace Nethermind.Synchronization.DbTuner;

public class SyncDbTuner
{
    private readonly IDb _stateDb;
    private readonly IDb _codeDb;
    private readonly IDb _blockDb;
    private readonly IDb _receiptDb;

    private ITunableDb.TuneType _tuneType;
    private ITunableDb.TuneType _blocksDbTuneType;

    public SyncDbTuner(
        ISyncConfig syncConfig,
        ISyncFeed<SnapSyncBatch>? snapSyncFeed,
        ISyncFeed<VerkleSyncBatch>? verkleSyncFeed,
        ISyncFeed<BodiesSyncBatch>? bodiesSyncFeed,
        ISyncFeed<ReceiptsSyncBatch>? receiptSyncFeed,
        IDb stateDb,
        IDb codeDb,
        IDb blockDb,
        IDb receiptDb
    )
    {
        // Only these three make sense as they are write heavy
        // Headers is used everywhere, so slowing read might slow the whole sync.
        // Statesync is read heavy, Forward sync is just plain too slow to saturate IO.
        if (snapSyncFeed != null)
        {
            snapSyncFeed.StateChanged += SnapStateChanged;
        }

        if (verkleSyncFeed != null)
        {
            verkleSyncFeed.StateChanged += VerkleStateChanged;
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
        _receiptDb = receiptDb;

        _tuneType = syncConfig.TuneDbMode;
        _blocksDbTuneType = syncConfig.BlocksDbTuneDbMode;
    }

    private void SnapStateChanged(object? sender, SyncFeedStateEventArgs e)
    {
        if (e.NewState == SyncFeedState.Active)
        {
            if (_stateDb is ITunableDb stateDb)
            {
                stateDb.Tune(_tuneType);
            }
            if (_codeDb is ITunableDb codeDb)
            {
                codeDb.Tune(_tuneType);
            }
        }
        else if (e.NewState == SyncFeedState.Finished)
        {
            if (_stateDb is ITunableDb stateDb)
            {
                stateDb.Tune(ITunableDb.TuneType.Default);
            }
            if (_codeDb is ITunableDb codeDb)
            {
                codeDb.Tune(ITunableDb.TuneType.Default);
            }
        }
    }

    private void VerkleStateChanged(object? sender, SyncFeedStateEventArgs e)
    {
        if (e.NewState == SyncFeedState.Active)
        {
            if (_stateDb is ITunableDb stateDb)
            {
                stateDb.Tune(_tuneType);
            }
            if (_codeDb is ITunableDb codeDb)
            {
                codeDb.Tune(_tuneType);
            }
        }
        else if (e.NewState == SyncFeedState.Finished)
        {
            if (_stateDb is ITunableDb stateDb)
            {
                stateDb.Tune(ITunableDb.TuneType.Default);
            }
            if (_codeDb is ITunableDb codeDb)
            {
                codeDb.Tune(ITunableDb.TuneType.Default);
            }
        }
    }

    private void BodiesStateChanged(object? sender, SyncFeedStateEventArgs e)
    {
        if (e.NewState == SyncFeedState.Active)
        {
            if (_blockDb is ITunableDb blockDb)
            {
                blockDb.Tune(_blocksDbTuneType);
            }
        }
        else if (e.NewState == SyncFeedState.Finished)
        {
            if (_blockDb is ITunableDb blockDb)
            {
                blockDb.Tune(ITunableDb.TuneType.Default);
            }
        }
    }

    private void ReceiptsStateChanged(object? sender, SyncFeedStateEventArgs e)
    {
        if (e.NewState == SyncFeedState.Active)
        {
            if (_receiptDb is ITunableDb receiptDb)
            {
                receiptDb.Tune(_tuneType);
            }
        }
        else if (e.NewState == SyncFeedState.Finished)
        {
            if (_receiptDb is ITunableDb receiptDb)
            {
                receiptDb.Tune(ITunableDb.TuneType.Default);
            }
        }
    }
}
