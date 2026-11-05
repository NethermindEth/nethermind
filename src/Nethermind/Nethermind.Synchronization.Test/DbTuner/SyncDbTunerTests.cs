// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Db;
using Nethermind.Synchronization.DbTuner;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.DbTuner;

public class SyncDbTunerTests
{
    private ITunableDb.TuneType _tuneType = ITunableDb.TuneType.HeavyWrite;
    private readonly ITunableDb.TuneType _blocksTuneType = ITunableDb.TuneType.AggressiveHeavyWrite;
    private SyncConfig _syncConfig = null!;
    private ISyncFeed<BodiesSyncBatch> _bodiesSyncFeed = null!;
    private ISyncFeed<ReceiptsSyncBatch> _receiptSyncFeed = null!;
    private ITunableDb _blockDb = null!;
    private ITunableDb _receiptDb = null!;

    [SetUp]
    public void Setup()
    {
        _tuneType = ITunableDb.TuneType.HeavyWrite;
        _syncConfig = new SyncConfig()
        {
            TuneDbMode = _tuneType,
            BlocksDbTuneDbMode = _blocksTuneType,
        };
        _bodiesSyncFeed = Substitute.For<ISyncFeed<BodiesSyncBatch>>();
        _receiptSyncFeed = Substitute.For<ISyncFeed<ReceiptsSyncBatch>>();
        _blockDb = Substitute.For<ITunableDb>();
        _receiptDb = Substitute.For<ITunableDb>();

        SyncDbTuner _ = new(
            _syncConfig,
            _bodiesSyncFeed,
            _receiptSyncFeed,
            _blockDb,
            _receiptDb);
    }

    [Test]
    public void WhenBodiesIsOn_TriggerBlocksDbTune() => TestFeedAndDbTune(_bodiesSyncFeed, _blockDb, _blocksTuneType);

    [Test]
    public void WhenReceiptsIsOn_TriggerReceiptsDbTune() => TestFeedAndDbTune(_receiptSyncFeed, _receiptDb);

    private void TestFeedAndDbTune<T>(ISyncFeed<T> feed, ITunableDb db, ITunableDb.TuneType? tuneType = null)
    {
        feed.StateChanged += Raise.EventWith(new SyncFeedStateEventArgs(SyncFeedState.Active));

        db.Received().Tune(tuneType ?? _tuneType);

        feed.StateChanged += Raise.EventWith(new SyncFeedStateEventArgs(SyncFeedState.Finished));

        db.Received().Tune(ITunableDb.TuneType.Default);
    }
}
