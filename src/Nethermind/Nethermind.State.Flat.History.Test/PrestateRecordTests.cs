// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Prestate;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

[NonParallelizable]
public class PrestateRecordTests
{
    private static readonly StateId Parent = new(9, TestItem.KeccakA);
    private static readonly Address First = new("0x0000000000000000000000000000000000000001");
    private static readonly Address Second = new("0x0000000000000000000000000000000000000002");
    private static readonly Address Third = new("0x0000000000000000000000000000000000000003");

    [Test]
    public void Record_RoundTrip_AnswersEveryCapturedKeyAndOnlyThose()
    {
        Account contract = new(7, 1_000_000, TestItem.KeccakB, TestItem.KeccakC);
        byte[] bytes = Encode(capture =>
        {
            capture.OnAccountRead(First, contract);
            capture.OnAccountRead(Second, null);
            capture.OnSlotRead(First, 1, 5);
            capture.OnSlotRead(First, 2, 0);
            capture.OnSlotRead(Third, 9, 0x0102);
        });

        using PrestateRecord record = PrestateRecord.TryOpen(bytes, Parent)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.TryGetAccount(First, out Account? first), Is.True);
            Assert.That(first!.Nonce, Is.EqualTo(7UL));
            Assert.That(first.Balance, Is.EqualTo((UInt256)1_000_000));
            Assert.That(first.StorageRoot, Is.EqualTo(TestItem.KeccakB));
            Assert.That(first.CodeHash, Is.EqualTo(TestItem.KeccakC));
            Assert.That(record.TryGetAccount(Second, out Account? second), Is.True, "a read of a missing account is a held value");
            Assert.That(second, Is.Null);
            Assert.That(record.TryGetAccount(Third, out _), Is.False, "only the slots of the third account were read, not its value");
            Assert.That(record.TryGetAccount(TestItem.AddressF, out _), Is.False, "an account that was never read is not in the record");
            Assert.That(record.TryGetSlot(First, 1, out UInt256 one), Is.True);
            Assert.That(one, Is.EqualTo((UInt256)5));
            Assert.That(record.TryGetSlot(First, 2, out UInt256 zero), Is.True, "a zero read is held, so history is not asked");
            Assert.That(zero, Is.EqualTo(UInt256.Zero));
            Assert.That(record.TryGetSlot(First, 3, out _), Is.False);
            Assert.That(record.TryGetSlot(Third, 9, out UInt256 third), Is.True);
            Assert.That(third, Is.EqualTo((UInt256)0x0102));
            Assert.That(record.TryGetSlot(Second, 1, out _), Is.False);
            Assert.That(record.BlockNumber, Is.EqualTo(10UL));
        }
    }

    [Test]
    public void Record_ReadTwice_ReturnsTheSameAccountObject()
    {
        byte[] bytes = Encode(capture => capture.OnAccountRead(First, new Account(1, 2)));
        using PrestateRecord record = PrestateRecord.TryOpen(bytes, Parent)!;

        record.TryGetAccount(First, out Account? once);
        record.TryGetAccount(First, out Account? twice);

        Assert.That(twice, Is.SameAs(once), "an account is created once per open, not once per read");
    }

    private static IEnumerable<TestCaseData> Corruptions()
    {
        yield return Case("Truncated", static b => b[..^1]);
        yield return Case("TrailingByte", static b => [.. b, 0]);
        yield return Case("UnknownVersion", static b => Set(b, PrestateRecordFormat.VersionOffset, 2));
        yield return Case("RootsFlagClear", static b => Set(b, PrestateRecordFormat.FlagsOffset, 0));
        yield return Case("SenderCountWithoutSection", static b => Set(b, PrestateRecordFormat.SenderCountOffset, 1));
        yield return Case("ReservedNotZero", static b => Set(b, PrestateRecordFormat.ReservedOffset + 3, 1));
        yield return Case("ParentNumberDiffers", static b => Set(b, PrestateRecordFormat.ParentNumberOffset, 8));
        yield return Case("ParentRootDiffers", static b => Set(b, PrestateRecordFormat.ParentRootOffset, (byte)(b[PrestateRecordFormat.ParentRootOffset] ^ 1)));
        yield return Case("AccountCountBeyondEnd", static b => Set(b, PrestateRecordFormat.AccountCountOffset, 200));
        yield return Case("SlotCountMismatch", static b => Set(b, PrestateRecordFormat.SlotCountOffset, 3));
        yield return Case("UnsortedAccounts", static b => Set(b, SecondAddressEnd, 0));
        yield return Case("DuplicateAccount", static b => Set(b, SecondAddressEnd, 1));
        yield return Case("UnsortedSlots", static b => Set(b, FirstSlotIndexEnd, 3));
        yield return Case("DuplicateSlot", static b => Set(b, FirstSlotIndexEnd, 2));
        yield return Case("NonMinimalVarInt", static b => Set(Set(b, FirstNonce, 0x81), FirstNonce + 1, 0));
        yield return Case("NonMinimalBalance", static b => Set(b, FirstNonce + 2, 0));
        yield return Case("UnknownAccountFlag", static b => Set(b, FirstFlags, 0x29));
        yield return Case("SlotsOnlyAccountWithValue", static b => Set(b, FirstFlags, 0x19));
        yield return Case("ValueLongerThanAWord", static b => Set(b, FirstSlotIndexEnd + 1, 33));
        yield return Case("PaddingByte2NotZero", static b => Set(b, PrestateRecordFormat.PaddingOffset, 1));
        yield return Case("PaddingByte3NotZero", static b => Set(b, PrestateRecordFormat.PaddingOffset + 1, 1));
        yield return Case("SlotsWithoutAccounts", static b => Set(b, PrestateRecordFormat.AccountCountOffset, 0));
        yield return Case("SlotCountZero", static b => Set(b, FirstSlotCount, 0));
        yield return Case("NotRecordedWithoutSlots", static b => Set(b, FirstFlags, PrestateRecordFormat.AccountValueNotRecorded));
        yield return Case("AbsentAccountWithCode", static b => Set(b, SecondAddressEnd + 1, PrestateRecordFormat.AccountHasCode));
        yield return Case("AbsentAccountWithRoot", static b => Set(b, SecondAddressEnd + 1, PrestateRecordFormat.AccountHasStorageRoot));
        yield return Case("OverlongVarInt", static b =>
        {
            byte[] copy = (byte[])b.Clone();
            copy.AsSpan(FirstNonce, PrestateRecordFormat.MaxVarIntLength + 1).Fill(0xFF);
            return copy;
        });
        yield return Case("ShortHeader", static b => b[..(PrestateRecordFormat.HeaderLength - 1)]);
    }

    /// <remarks>
    /// The valid record holds two accounts: the first exists (nonce 1, balance 0x0100) with slots 1 = 5 and 2 = 0, the
    /// second is absent with no slots. Offsets: first flags 148, nonce 149, balance 150-152, slot count 153, slot 1
    /// index 154-185, its length 186, slot 2 index 188-219, second account address 221-240.
    /// </remarks>
    [TestCaseSource(nameof(Corruptions))]
    public void Record_WhenMalformedOrOfAnotherState_IsNotOpened(Func<byte[], byte[]> corrupt)
    {
        byte[] valid = ValidTwoAccountRecord();
        Assert.That(PrestateRecord.TryOpen(valid, Parent), Is.Not.Null, "precondition: the unmodified record opens");
        byte[] corrupted = corrupt(valid);

        PrestateRecord? opened = PrestateRecord.TryOpen(corrupted, Parent);

        Assert.That(opened, Is.Null, "a malformed record must never answer a read");
    }

    /// <remarks>The account has nonce 1, a zero balance and no slots, so its code hash starts at offset 151 and its storage
    /// root right after it.</remarks>
    [Test]
    public void Record_WithAnEmptyHashWrittenAsBytes_IsNotOpened([Values] bool codeHash)
    {
        byte[] valid = Encode(capture => capture.OnAccountRead(First, new Account(1, 0, TestItem.KeccakB, TestItem.KeccakC)));
        Assert.That(PrestateRecord.TryOpen(valid, Parent), Is.Not.Null, "precondition: the unmodified record opens");
        byte[] corrupted = (byte[])valid.Clone();
        int offset = FirstNonce + 2 + (codeHash ? 0 : Hash256.Size);
        (codeHash ? Keccak.OfAnEmptyString : Keccak.EmptyTreeHash).Bytes.CopyTo(corrupted.AsSpan(offset));

        PrestateRecord? opened = PrestateRecord.TryOpen(corrupted, Parent);

        Assert.That(opened, Is.Null, "an empty hash is a cleared flag, never bytes");
    }

    [Test]
    public void Encode_AtTheExactSizeCap_EncodesAndOneByteLessDoesNot()
    {
        PrestateCapture capture = Captured(c =>
        {
            c.OnAccountRead(First, new Account(3, 1_000_000, TestItem.KeccakB, TestItem.KeccakC));
            c.OnSlotRead(First, 1, 0x0102);
            c.OnSlotRead(Second, 7, 0);
        });
        Assert.That(capture.TryEncode(10, TestItem.KeccakH, 1 << 20, out byte[]? unbounded, out int length), Is.True, "precondition: the capture encodes");
        byte[] reference = unbounded.AsSpan(0, length).ToArray();

        bool exact = capture.TryEncode(10, TestItem.KeccakH, length, out byte[]? atCap, out int atCapLength);
        bool under = capture.TryEncode(10, TestItem.KeccakH, length - 1, out _, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exact, Is.True, "the measured size is the encoded size, so a cap equal to it fits");
            Assert.That(atCap.AsSpan(0, atCapLength).ToArray(), Is.EqualTo(reference));
            Assert.That(under, Is.False);
            using PrestateRecord record = PrestateRecord.TryOpen(reference, Parent)!;
            Assert.That(record.BlockHash, Is.EqualTo(TestItem.KeccakH.ValueHash256), "the header carries the block hash");
        }
    }

    [Test]
    public void Recorder_WhenTheCaptureIsNotOfTheBlocksParent_WritesNothing()
    {
        using MemDb db = new();
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        PrestateRecordStore store = new(db);
        PrestateRecorder recorder = new(store, new HistoryAvailability(columns.GetColumnDb(FlatHistoryColumns.AvailableBlocks)),
            new FlatDbConfig { HistoryPrestateRecordsEnabled = true }, LimboLogs.Instance);
        PrestateCapture capture = Captured(c => c.OnAccountRead(First, null));

        bool written = recorder.TryWrite(capture, Parent.BlockNumber + 2, TestItem.KeccakH.ValueHash256, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.False, "a record is keyed by the parent, so a capture of another state must not be stored under it");
            Assert.That(store.Read(Parent, out bool found), Is.Null);
            Assert.That(found, Is.False);
        }
    }

    [TestCase(0UL, null, null, 0UL, TestName = "PruneFloor_KeepAllWithoutHistoryFloor_IsZero")]
    [TestCase(0UL, 10UL, 1000UL, 11UL, TestName = "PruneFloor_KeepAll_FollowsTheHistoryFloor")]
    [TestCase(100UL, 10UL, 1000UL, 900UL, TestName = "PruneFloor_Window_IsWatermarkMinusRetention")]
    [TestCase(100UL, 10UL, 100UL, 11UL, TestName = "PruneFloor_WatermarkEqualToRetention_FollowsTheHistoryFloor")]
    [TestCase(100UL, 950UL, 1000UL, 951UL, TestName = "PruneFloor_HistoryFloorAboveTheWindow_Wins")]
    public void PruneFloor_FromRetentionAndAvailability(ulong retention, ulong? historyFloor, ulong? watermark, ulong expected)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        HistoryAvailability availability = new(columns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        if (watermark is { } mark) Publish(columns, availability, mark);
        if (historyFloor is { } floor) availability.PublishGlobalFloor(floor);
        using MemDb db = new();
        PrestateRecorder recorder = new(new PrestateRecordStore(db), availability, new FlatDbConfig { HistoryPrestateRecordsRetentionBlocks = retention }, LimboLogs.Instance);

        Assert.That(recorder.PruneFloor(), Is.EqualTo(expected));
    }

    [Test]
    public void Prune_WithinTheInterval_DoesNotPruneAgain()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        HistoryAvailability availability = new(columns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        Publish(columns, availability, 5000);
        availability.PublishGlobalFloor(10);
        using MemDb db = new();
        PrestateRecordStore store = new(db);
        PrestateRecorder recorder = new(store, availability, new FlatDbConfig { HistoryPrestateRecordsRetentionBlocks = 0 }, LimboLogs.Instance);

        recorder.Prune(2000, CancellationToken.None);
        ulong first = store.Floor;
        availability.PublishGlobalFloor(20);
        recorder.Prune(2000 + PrestateRecorder.PruneIntervalBlocks - 1, CancellationToken.None);
        ulong withinInterval = store.Floor;
        recorder.Prune(2000 + PrestateRecorder.PruneIntervalBlocks, CancellationToken.None);

        Assert.That((first, withinInterval, store.Floor), Is.EqualTo((11UL, 11UL, 21UL)), "pruning runs once per interval");
    }

    [Test]
    public void Store_PruneBelowALowerFloor_KeepsTheHigherOneAndTheCacheFollowsIt()
    {
        using MemDb db = new();
        PrestateRecordStore store = new(db);
        StateId nine = new(9, TestItem.KeccakB);
        StateId ten = new(10, TestItem.KeccakC);
        store.Write(nine, Encode(capture => capture.OnAccountRead(First, null), nine));
        store.Write(ten, Encode(capture => capture.OnAccountRead(First, null), ten));
        PrestateRecordCache cache = new(store, new FlatDbConfig(), LimboLogs.Instance);
        Assert.That(cache.TryLease(nine, out PrestateRecord? cachedNine), Is.True, "precondition: the record below the coming floor is cached");
        cachedNine!.Dispose();

        store.PruneBelow(10, reclaim: false);
        store.PruneBelow(5, reclaim: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Floor, Is.EqualTo(10UL), "the floor never moves down");
            Assert.That(store.Read(nine, out _), Is.Null);
            using PrestateRecord? kept = store.Read(ten, out _);
            Assert.That(kept, Is.Not.Null, "the record at the floor stays");
            Assert.That(cache.TryLeaseCached(nine, out _), Is.False, "a cached record below the floor is not handed out");
            Assert.That(cache.TryLease(nine, out _), Is.False);
        }
    }

    [Test]
    public void Cache_OverBudget_EvictsTheLeastRecentlyUsed()
    {
        using MemDb db = new();
        PrestateRecordStore store = new(db);
        StateId a = new(10, TestItem.KeccakB);
        StateId b = new(11, TestItem.KeccakC);
        StateId c = new(12, TestItem.KeccakD);
        foreach (StateId state in new[] { a, b, c }) store.Write(state, Encode(capture => capture.OnAccountRead(First, null), state));
        long size;
        using (PrestateRecord probe = store.Read(a, out _)!) size = probe.ByteSize;
        PrestateRecordCache cache = new(store, new FlatDbConfig { HistoryPrestateRecordsCacheBytes = 2 * size }, LimboLogs.Instance);

        Lease(cache, a);
        Lease(cache, b);
        Lease(cache, a);
        Lease(cache, c);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryLeaseCached(b, out _), Is.False, "the least recently used record goes first");
            Assert.That(cache.TryLeaseCached(a, out PrestateRecord? keptA), Is.True, "a re-leased record counts as recent");
            keptA?.Dispose();
            Assert.That(cache.TryLeaseCached(c, out PrestateRecord? keptC), Is.True);
            keptC?.Dispose();
            Assert.That(cache.Bytes, Is.EqualTo(2 * size), "the evicted record's bytes are released from the budget");
        }
    }

    [Test]
    public void Capture_ReadTwice_KeepsTheFirstValue()
    {
        byte[] bytes = Encode(capture =>
        {
            capture.OnAccountRead(First, new Account(1, 1));
            capture.OnAccountRead(First, new Account(2, 2));
            capture.OnSlotRead(First, 1, 1);
            capture.OnSlotRead(First, 1, 2);
        });
        using PrestateRecord record = PrestateRecord.TryOpen(bytes, Parent)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.TryGetAccount(First, out Account? account) && account!.Nonce == 1, Is.True, "the parent value is the first one the scope returned");
            Assert.That(record.TryGetSlot(First, 1, out UInt256 slot) && slot == 1, Is.True);
        }
    }

    [Test]
    public void Capture_KeyWrittenBeforeItsFirstRead_IsNotRecorded()
    {
        byte[] bytes = Encode(capture =>
        {
            capture.OnAccountWritten(First, removedWithStorage: false);
            capture.OnAccountRead(First, new Account(5, 5));
            capture.OnStorageWritten(Second);
            capture.OnSlotRead(Second, 1, 9);
            capture.OnAccountRead(Second, new Account(3, 3));
        });
        using PrestateRecord record = PrestateRecord.TryOpen(bytes, Parent)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.TryGetAccount(First, out _), Is.False, "a value read after a write is not the parent value");
            Assert.That(record.TryGetSlot(Second, 1, out _), Is.False, "a slot read after its write is not the parent value");
            Assert.That(record.TryGetAccount(Second, out _), Is.False, "a slot write is a write to its account");
        }
    }

    [Test]
    public void Capture_AfterAnAccountRemovalOrStorageClear_RecordsNoSlotOfIt()
    {
        byte[] bytes = Encode(capture =>
        {
            capture.OnSlotRead(First, 1, 4);
            capture.OnAccountWritten(First, removedWithStorage: true);
            capture.OnSlotRead(First, 2, 6);
            capture.OnStorageWritten(Second);
            capture.OnSlotRead(Second, 1, 8);
        });
        using PrestateRecord record = PrestateRecord.TryOpen(bytes, Parent)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.TryGetSlot(First, 1, out UInt256 before) && before == 4, Is.True, "a read before the removal is the parent value");
            Assert.That(record.TryGetSlot(First, 2, out _), Is.False, "a null account removes its storage with it");
            Assert.That(record.TryGetSlot(Second, 1, out _), Is.False, "a clear hides every later slot read of the account");
        }
    }

    [Test]
    public void Capture_ReadsAfterCommit_AreIgnored()
    {
        byte[] bytes = Encode(static _ => { }, after: capture => capture.OnAccountRead(First, new Account(1, 1)));
        using PrestateRecord record = PrestateRecord.TryOpen(bytes, Parent)!;

        Assert.That(record.TryGetAccount(First, out _), Is.False, "after the commit the scope holds the block's own writes");
    }

    [Test]
    public void Capture_ResetThenAnotherParent_HoldsNoKeyOfTheFirst()
    {
        PrestateCapture capture = new();
        using (capture.Arm())
        {
            capture.OnScopeOpened(new StateId(3, TestItem.KeccakD));
            capture.OnAccountRead(First, new Account(1, 1));
            capture.OnSlotRead(First, 1, 1);
        }

        byte[] bytes = Encode(capture, c => c.OnAccountRead(Second, new Account(2, 2)));
        using PrestateRecord record = PrestateRecord.TryOpen(bytes, Parent)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.TryGetAccount(First, out _), Is.False, "a cancelled capture must not leak into the next record");
            Assert.That(record.TryGetSlot(First, 1, out _), Is.False);
            Assert.That(record.TryGetAccount(Second, out _), Is.True);
        }
    }

    [Test]
    public void Capture_WhenNotArmed_RecordsNothingAndIsNotComplete()
    {
        PrestateCapture capture = new();

        bool opened = capture.OnScopeOpened(Parent);
        capture.OnAccountRead(First, new Account(1, 1));
        capture.OnCommit();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opened, Is.False, "a scope opened between captures is not decorated");
            Assert.That(capture.IsComplete, Is.False);
        }
    }

    [Test]
    public void Capture_WhenASecondScopeOpens_Fails()
    {
        PrestateCapture capture = new();
        using PrestateCapture.ArmedCapture armed = capture.Arm();
        capture.OnScopeOpened(Parent);

        bool second = capture.OnScopeOpened(new StateId(4, TestItem.KeccakE));
        capture.OnCommit();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.False);
            Assert.That(capture.IsComplete, Is.False, "two scopes in one capture cannot be told apart");
        }
    }

    [Test]
    public void Capture_WhenTheRecordExceedsTheCap_EncodesNothing()
    {
        PrestateCapture capture = Captured(c =>
        {
            for (int i = 0; i < 10; i++) c.OnSlotRead(First, (UInt256)i, (UInt256)i + 1);
        });

        bool encoded = capture.TryEncode(10, ValueKeccak.Zero, PrestateRecordFormat.HeaderLength + 100, out byte[]? buffer, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.False);
            Assert.That(buffer, Is.Null);
        }
    }

    [TestCase(0, TestName = "Decorator_HintGet_FailsTheCapture")]
    [TestCase(1, TestName = "Decorator_HintBal_FailsTheCapture")]
    [TestCase(2, TestName = "Decorator_ApplyBal_FailsTheCapture")]
    public void Decorator_AccessListHintOrApply_FailsTheCapture(int call)
    {
        PrestateCapture capture = new();
        IWorldStateScopeProvider provider = capture.Decorate(InnerProvider(out _));
        using PrestateCapture.ArmedCapture armed = capture.Arm();
        IWorldStateScopeProvider.IScope scope = OpenScope(provider);

        switch (call)
        {
            case 0: scope.HintGet(First, null); break;
            case 1: scope.HintBal(null!); break;
            default: scope.ApplyBal(null!); break;
        }
        scope.Commit(10);

        Assert.That(capture.IsComplete, Is.False, "a value that did not come through Get cannot be proven to be the parent value");
    }

    [Test]
    public void Decorator_ReadsAndWrites_ReachTheCapture()
    {
        PrestateCapture capture = new();
        IWorldStateScopeProvider provider = capture.Decorate(InnerProvider(out IWorldStateScopeProvider.IScope inner));
        inner.Get(First).Returns(new Account(4, 4));
        using PrestateCapture.ArmedCapture armed = capture.Arm();
        IWorldStateScopeProvider.IScope scope = OpenScope(provider);

        scope.Get(First);
        scope.CreateStorageTree(Second).Get(1, out _);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(Third, null);
        }
        scope.CreateStorageTree(Third).Get(1, out _);
        scope.Commit(10);

        Assert.That(capture.IsComplete, Is.True, capture.Failure);
        bool encoded = capture.TryEncode(10, ValueKeccak.Zero, 1 << 20, out byte[]? buffer, out int length);
        Assert.That(encoded, Is.True, "precondition: the capture encodes");
        using PrestateRecord record = PrestateRecord.TryOpen(buffer.AsSpan(0, length), Parent)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.TryGetAccount(First, out Account? first) && first!.Nonce == 4, Is.True);
            Assert.That(record.TryGetSlot(Second, 1, out _), Is.True, "a storage-tree read reaches the capture");
            Assert.That(record.TryGetSlot(Third, 1, out _), Is.False, "the account was removed before the read");
        }
    }

    [Test]
    public void Decorator_ScopeClosedWithoutCommit_EndsTheCapture()
    {
        PrestateCapture capture = new();
        IWorldStateScopeProvider provider = capture.Decorate(InnerProvider(out IWorldStateScopeProvider.IScope inner));
        inner.Get(First).Returns(new Account(4, 4));
        using PrestateCapture.ArmedCapture armed = capture.Arm();
        IWorldStateScopeProvider.IScope scope = OpenScope(provider);
        scope.Get(First);

        scope.Dispose();
        capture.OnAccountRead(Second, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capture.IsComplete, Is.True, "a read-only replay never commits its scope; closing it ends the block");
            Assert.That(capture.TryEncode(10, ValueKeccak.Zero, 1 << 20, out byte[]? buffer, out int length), Is.True);
            using PrestateRecord record = PrestateRecord.TryOpen(buffer.AsSpan(0, length), Parent)!;
            Assert.That(record.TryGetAccount(Second, out _), Is.False, "nothing is recorded after the scope closed");
        }
    }

    [Test]
    public void Decorator_BetweenCaptures_ReturnsTheInnerScope()
    {
        PrestateCapture capture = new();
        IWorldStateScopeProvider provider = capture.Decorate(InnerProvider(out IWorldStateScopeProvider.IScope inner));

        IWorldStateScopeProvider.IScope scope = OpenScope(provider);

        Assert.That(scope, Is.SameAs(inner), "a capture that is not armed must cost the execution nothing");
    }

    [Test]
    public void Store_PruneBelow_DropsOlderRecordsAndKeepsTheFloor()
    {
        using MemDb db = new();
        PrestateRecordStore store = new(db);
        StateId old = new(5, TestItem.KeccakB);
        StateId kept = new(20, TestItem.KeccakC);
        store.Write(old, Encode(capture => capture.OnAccountRead(First, null), old));
        store.Write(kept, Encode(capture => capture.OnAccountRead(First, null), kept));

        store.PruneBelow(10, reclaim: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Read(old, out bool oldFound), Is.Null);
            Assert.That(oldFound, Is.False);
            using PrestateRecord? record = store.Read(kept, out _);
            Assert.That(record, Is.Not.Null);
            Assert.That(new PrestateRecordStore(db).Floor, Is.EqualTo(10UL), "the floor is durable and outside the deleted range");
        }
    }

    [Test]
    public void Cache_AbsentThenWrittenAndInvalidated_FindsTheRecord([Values] bool invalidate)
    {
        using MemDb db = new();
        PrestateRecordStore store = new(db);
        PrestateRecordCache cache = new(store, new FlatDbConfig(), LimboLogs.Instance);
        Assert.That(cache.TryLease(Parent, out _), Is.False, "precondition: nothing written yet");

        store.Write(Parent, Encode(capture => capture.OnAccountRead(First, null)));
        if (invalidate) cache.Invalidate(Parent);

        bool found = cache.TryLease(Parent, out PrestateRecord? record);
        Assert.That(found, Is.EqualTo(invalidate), "the absence is remembered until the writer invalidates exactly that state");
        if (!invalidate) return;
        record!.Dispose();
    }

    [Test]
    public void Cache_EvictionDuringALease_LeavesTheLeasedRecordReadable()
    {
        using MemDb db = new();
        PrestateRecordStore store = new(db);
        StateId other = new(11, TestItem.KeccakB);
        store.Write(Parent, Encode(capture => capture.OnAccountRead(First, new Account(1, 1))));
        store.Write(other, Encode(capture => capture.OnAccountRead(First, null), other));
        PrestateRecordCache cache = new(store, new FlatDbConfig { HistoryPrestateRecordsCacheBytes = 1 }, LimboLogs.Instance);

        cache.TryLease(Parent, out PrestateRecord? leased);
        cache.TryLease(other, out PrestateRecord? evicting);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Bytes, Is.LessThanOrEqualTo(1L), "the budget evicts both entries");
            Assert.That(leased!.TryGetAccount(First, out Account? account) && account!.Nonce == 1, Is.True, "a reader keeps its lease after eviction");
        }
        leased!.Dispose();
        evicting!.Dispose();
    }

    [Test]
    public void Cache_WhenTheStoreThrows_AnswersNoRecord()
    {
        using FailingDb db = new();
        PrestateRecordCache cache = new(new PrestateRecordStore(db), new FlatDbConfig(), LimboLogs.Instance);
        db.Fail = true;

        Assert.That(cache.TryLease(Parent, out _), Is.False, "a lookup never throws into the read path");
    }

    private static void Lease(PrestateRecordCache cache, in StateId state)
    {
        Assert.That(cache.TryLease(state, out PrestateRecord? record), Is.True, "precondition: the record is leased");
        record!.Dispose();
    }

    private static void Publish(SnapshotableMemColumnsDb<FlatHistoryColumns> columns, HistoryAvailability availability, ulong watermark)
    {
        HistoryColumnsWriter.MarkBlock(columns, watermark, ValueKeccak.Zero);
        availability.PublishWatermark(watermark, HistoryAvailability.FormatVersion);
    }

    private static byte[] ValidTwoAccountRecord() => Encode(capture =>
    {
        capture.OnAccountRead(First, new Account(1, 0x0100));
        capture.OnSlotRead(First, 1, 5);
        capture.OnSlotRead(First, 2, 0);
        capture.OnAccountRead(Second, null);
    });

    private const int FirstFlags = PrestateRecordFormat.HeaderLength + 20;
    private const int FirstNonce = FirstFlags + 1;
    private const int FirstSlotCount = FirstNonce + 1 + 3;
    private const int FirstSlotIndexEnd = FirstNonce + 1 + 3 + 1 + 31;
    private const int SecondAddressEnd = FirstSlotIndexEnd + 1 + 1 + 1 + 32 + 1 + 19;

    private static TestCaseData Case(string name, Func<byte[], byte[]> corrupt) => new TestCaseData(corrupt).SetName($"Record_WhenMalformedOrOfAnotherState_IsNotOpened({name})");

    private static byte[] Set(byte[] bytes, int offset, byte value)
    {
        byte[] copy = (byte[])bytes.Clone();
        copy[offset] = value;
        return copy;
    }

    internal static byte[] Encode(Action<PrestateCapture> record, StateId? parent = null, Action<PrestateCapture>? after = null) =>
        Encode(new PrestateCapture(), record, parent, after);

    private static byte[] Encode(PrestateCapture capture, Action<PrestateCapture> record, StateId? parent = null, Action<PrestateCapture>? after = null)
    {
        Captured(capture, record, parent ?? Parent, after);
        Assert.That(capture.TryEncode(10, TestItem.KeccakH, 1 << 20, out byte[]? buffer, out int length), Is.True, "precondition: the capture encodes");
        return buffer.AsSpan(0, length).ToArray();
    }

    private static PrestateCapture Captured(Action<PrestateCapture> record) => Captured(new PrestateCapture(), record, Parent, null);

    private static PrestateCapture Captured(PrestateCapture capture, Action<PrestateCapture> record, StateId parent, Action<PrestateCapture>? after)
    {
        _ = capture.Arm();
        capture.OnScopeOpened(parent);
        record(capture);
        capture.OnCommit();
        after?.Invoke(capture);
        Assert.That(capture.IsComplete, Is.True, "precondition: the capture is complete");
        return capture;
    }

    private static IWorldStateScopeProvider.IScope OpenScope(IWorldStateScopeProvider provider)
    {
        Assert.That(provider.TryBeginScope(Build.A.BlockHeader.WithNumber(9).WithStateRoot(TestItem.KeccakA).TestObject, new LocalMetrics(),
            out IWorldStateScopeProvider.IScope? scope), Is.True, "precondition: the scope opens");
        return scope!;
    }

    private static IWorldStateScopeProvider InnerProvider(out IWorldStateScopeProvider.IScope scope)
    {
        IWorldStateScopeProvider provider = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider.IScope inner = Substitute.For<IWorldStateScopeProvider.IScope>();
        provider.TryBeginScope(Arg.Any<BlockHeader?>(), Arg.Any<LocalMetrics>(), out Arg.Any<IWorldStateScopeProvider.IScope?>())
            .Returns(call =>
            {
                call[2] = inner;
                return true;
            });
        scope = inner;
        return provider;
    }

    private sealed class FailingDb : MemDb
    {
        public bool Fail { get; set; }

        public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
            Fail ? throw new InvalidOperationException("disk failure") : base.Get(key, flags);
    }
}
