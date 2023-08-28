// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Db;
using Nethermind.Synchronization.DbTuner;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.VerkleSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.DbTuner;

public class SyncDbTunerTests
{
    private ITunableDb.TuneType _tuneType = ITunableDb.TuneType.HeavyWrite;
    private ITunableDb.TuneType _blocksTuneType = ITunableDb.TuneType.AggressiveHeavyWrite;
    private SyncConfig _syncConfig = null!;
    private ISyncFeed<SnapSyncBatch>? _snapSyncFeed;
    private ISyncFeed<VerkleSyncBatch>? _verkleSyncFeed;
    private ISyncFeed<BodiesSyncBatch>? _bodiesSyncFeed;
    private ISyncFeed<ReceiptsSyncBatch>? _receiptSyncFeed;
    private ITunableDb _stateDb = null!;
    private ITunableDb _codeDb = null!;
    private ITunableDb _blockDb = null!;
    private ITunableDb _receiptDb = null!;
    private SyncDbTuner _tuner = null!;

    [SetUp]
    public void Setup()
    {
        _tuneType = ITunableDb.TuneType.HeavyWrite;
        _syncConfig = new SyncConfig()
        {
            TuneDbMode = _tuneType,
            BlocksDbTuneDbMode = _blocksTuneType,
        };
        _snapSyncFeed = Substitute.For<ISyncFeed<SnapSyncBatch>?>();
        _verkleSyncFeed = Substitute.For<ISyncFeed<VerkleSyncBatch>?>();
        _bodiesSyncFeed = Substitute.For<ISyncFeed<BodiesSyncBatch>?>();
        _receiptSyncFeed = Substitute.For<ISyncFeed<ReceiptsSyncBatch>?>();
        _stateDb = Substitute.For<ITunableDb>();
        _codeDb = Substitute.For<ITunableDb>();
        _blockDb = Substitute.For<ITunableDb>();
        _receiptDb = Substitute.For<ITunableDb>();

        _tuner = new SyncDbTuner(
            _syncConfig,
            _snapSyncFeed,
            _verkleSyncFeed,
            _bodiesSyncFeed,
            _receiptSyncFeed,
            _stateDb,
            _codeDb,
            _blockDb,
            _receiptDb);
    }

    [Test]
    public void WhenSnapIsOn_TriggerStateDbTune()
    {
        TestFeedAndDbTune(_snapSyncFeed, _stateDb);
    }

    [Test]
    public void WhenSnapIsOn_TriggerCodeDbTune()
    {
        TestFeedAndDbTune(_snapSyncFeed, _codeDb);
    }

    [Test]
    public void WhenBodiesIsOn_TriggerBlocksDbTune()
    {
        TestFeedAndDbTune(_bodiesSyncFeed, _blockDb, _blocksTuneType);
    }

    [Test]
    public void WhenReceiptsIsOn_TriggerReceiptsDbTune()
    {
        TestFeedAndDbTune(_receiptSyncFeed, _receiptDb);
    }

    public void TestFeedAndDbTune<T>(ISyncFeed<T> feed, ITunableDb db, ITunableDb.TuneType? tuneType = null)
    {
        feed.StateChanged += Raise.EventWith(new SyncFeedStateEventArgs(SyncFeedState.Active));

        db.Received().Tune(tuneType ?? _tuneType);

        feed.StateChanged += Raise.EventWith(new SyncFeedStateEventArgs(SyncFeedState.Finished));

        db.Received().Tune(ITunableDb.TuneType.Default);
    }
}
