// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Storage;

/// <summary>
/// The canonical slot index is what by-range serving, the store replay and the API trust above finality, so after every
/// head change it must name exactly the head's ancestry: a slot the new chain skips, or one above a shorter new head,
/// must read as empty, not as the orphaned block that held it before.
/// </summary>
public class CanonicalIndexReorgTests
{
    [Test]
    public void Reorg_to_a_chain_that_skips_slots_clears_them()
    {
        CanonicalReorgFixture fixture = CanonicalReorgFixture.Create();
        fixture.ImportLongerChainA();
        CanonicalReorgFixture.Reorg reorg = fixture.ReorgToSkippingChainB();

        Assert.That(fixture.CanonicalRoots(through: 4), Is.EqualTo(new Hash256?[] { fixture.Chain.AnchorRoot, reorg.B1.Root, null, null, reorg.BHead.Root }),
            "slots 2 and 3 held chain A's orphans, which chain B skips");
    }

    [Test]
    public void Reorg_to_a_shorter_chain_clears_the_slots_above_its_head()
    {
        CanonicalReorgFixture fixture = CanonicalReorgFixture.Create();
        fixture.ImportLongerChainA();
        UnsignedChain.ChainBlock b1 = fixture.Import(fixture.Chain.AnchorRoot, slot: 1, 0xb1);
        UnsignedChain.ChainBlock b2 = fixture.Import(b1.Root, slot: 2, 0xb2, [fixture.Chain.Vote(1, b1.Root)]);

        Assert.That(fixture.ComputeHead(), Is.EqualTo(b2.Root), "one vote puts chain B ahead of the unvoted chain A");
        Assert.That(fixture.CanonicalRoots(through: 3), Is.EqualTo(new Hash256?[] { fixture.Chain.AnchorRoot, b1.Root, b2.Root, null }),
            "slot 3 held chain A's head, above chain B's head");
        Assert.That(fixture.Store.GetCanonicalIndexTopSlot(), Is.EqualTo(2UL), "the recorded top slot follows the head down");
    }

    [Test]
    public void Head_advance_without_a_reorg_writes_only_the_new_slots()
    {
        CanonicalReorgFixture fixture = CanonicalReorgFixture.Create();
        UnsignedChain.ChainBlock a1 = fixture.Import(fixture.Chain.AnchorRoot, slot: 1, 0xa1);
        UnsignedChain.ChainBlock a2 = fixture.Import(a1.Root, slot: 2, 0xa2);
        fixture.ComputeHead();
        UnsignedChain.ChainBlock a4 = fixture.Import(a2.Root, slot: 4, 0xa4);

        Assert.That(fixture.ComputeHead(), Is.EqualTo(a4.Root));
        Assert.That(fixture.ComputeHead(), Is.EqualTo(a4.Root));
        TestMemDb index = fixture.BlockIndex;
        Assert.Multiple(() =>
        {
            foreach (ulong slot in new ulong[] { 0, 1, 2, 4 })
            {
                index.KeyWasWritten(CanonicalReorgFixture.SlotKey(slot), times: 1);
            }

            index.KeyWasWritten(static entry => entry.Item1.Length == sizeof(ulong), times: 4);
            index.KeyWasRemoved(static key => key.Length == sizeof(ulong), times: 0);
            // The advance stops one block below the old head, so the anchor slot is read only by the first head change.
            index.KeyWasRead(CanonicalReorgFixture.SlotKey(0), times: 1);
            // A head that did not change is not walked again.
            index.KeyWasRead(CanonicalReorgFixture.SlotKey(4), times: 1);
        });
    }

    [Test]
    public void Stale_entries_in_skipped_slots_are_cleared_until_a_run_of_skipped_slots_is_empty()
    {
        CanonicalReorgFixture fixture = CanonicalReorgFixture.Create();
        UnsignedChain.ChainBlock a2 = fixture.Import(fixture.Chain.AnchorRoot, slot: 2, 0xa2);
        UnsignedChain.ChainBlock a4 = fixture.Import(a2.Root, slot: 4, 0xa4);
        fixture.ComputeHead();
        // Entries an index that never cleared skipped slots left behind.
        fixture.Store.SetCanonicalRoot(1, Keccak.Compute("stale 1"));
        fixture.Store.SetCanonicalRoot(3, Keccak.Compute("stale 3"));
        UnsignedChain.ChainBlock a5 = fixture.Import(a4.Root, slot: 5, 0xa5);

        Assert.That(fixture.ComputeHead(), Is.EqualTo(a5.Root));
        Assert.That(fixture.CanonicalRoots(through: 5), Is.EqualTo(new Hash256?[] { fixture.Chain.AnchorRoot, null, a2.Root, null, a4.Root, a5.Root }),
            "skipped slots below a canonical block that still held an entry must not end the walk");
    }

    [Test]
    public void Index_changes_set_and_clear_slots_and_record_the_top_slot_together()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        store.SetCanonicalRoot(7, Keccak.Compute("seven"));
        store.SetCanonicalRoot(8, Keccak.Compute("eight"));

        store.ApplyCanonicalIndexChanges([(7, null), (9, Keccak.Compute("nine"))], topSlot: 9);

        Assert.Multiple(() =>
        {
            Assert.That(store.TryGetCanonicalRoot(7, out _), Is.False);
            Assert.That(store.TryGetCanonicalRoot(8, out Hash256? eight) && eight == Keccak.Compute("eight"), Is.True, "a slot the change does not name is kept");
            Assert.That(store.TryGetCanonicalRoot(9, out Hash256? nine) && nine == Keccak.Compute("nine"), Is.True);
            Assert.That(store.GetCanonicalIndexTopSlot(), Is.EqualTo(9UL));
        });
    }

    /// <summary>
    /// A head change the store fails to commit, as a crash before the commit would, must leave the previous index whole:
    /// a half-written index mixes two chains, and a later walk that meets an entry of the new chain trusts everything below it.
    /// </summary>
    [Test]
    public void Head_change_that_fails_to_commit_leaves_the_previous_index_and_the_next_one_writes_it_whole()
    {
        CanonicalReorgFixture fixture = CanonicalReorgFixture.Create();
        UnsignedChain.ChainBlock[] chainA = fixture.ImportLongerChainA();
        UnsignedChain.ChainBlock b1 = fixture.Import(fixture.Chain.AnchorRoot, slot: 1, 0xb1);
        UnsignedChain.ChainBlock b4 = fixture.Import(b1.Root, slot: 4, 0xb4, [fixture.Chain.Vote(1, b1.Root), fixture.Chain.Vote(3, b1.Root)]);

        fixture.Db.FailNextCommit = true;
        Assert.Throws<IOException>(() => fixture.ComputeHead());
        Hash256?[] afterFailure = fixture.CanonicalRoots(through: 4);
        Hash256 head = fixture.ComputeHead();

        Assert.Multiple(() =>
        {
            Assert.That(afterFailure, Is.EqualTo(new Hash256?[] { fixture.Chain.AnchorRoot, chainA[0].Root, chainA[1].Root, chainA[2].Root, null }),
                "nothing of chain B is written until the whole change commits");
            Assert.That(head, Is.EqualTo(b4.Root));
            Assert.That(fixture.CanonicalRoots(through: 4), Is.EqualTo(new Hash256?[] { fixture.Chain.AnchorRoot, b1.Root, null, null, b4.Root }));
        });
    }

    public enum IndexedChain
    {
        Original,
        Skipping,
        Shorter,
    }

    [TestCase(IndexedChain.Original, 1, TestName = "First_head_change_after_a_restart_clears_what_the_previous_run_indexed_above_it")]
    [TestCase(IndexedChain.Skipping, 0, TestName = "Restart_that_replays_nothing_leaves_nothing_above_the_anchor")]
    [TestCase(IndexedChain.Skipping, 1, TestName = "Restart_after_a_reorg_onto_a_skipping_chain_indexes_only_the_replayed_chain(False)")]
    [TestCase(IndexedChain.Skipping, 4, TestName = "Restart_after_a_reorg_onto_a_skipping_chain_indexes_only_the_replayed_chain(True)")]
    [TestCase(IndexedChain.Shorter, 1, TestName = "Restart_after_a_reorg_to_a_lower_head_leaves_nothing_above_the_replayed_head(False)")]
    [TestCase(IndexedChain.Shorter, 2, TestName = "Restart_after_a_reorg_to_a_lower_head_leaves_nothing_above_the_replayed_head(True)")]
    public void Restart_indexes_only_the_replayed_ancestry(IndexedChain indexed, int replayThrough)
    {
        CanonicalReorgFixture fixture = CanonicalReorgFixture.Create();
        UnsignedChain.ChainBlock[] chainA = fixture.ImportLongerChainA();
        CanonicalReorgFixture.Reorg? skipping = null;
        UnsignedChain.ChainBlock? oldFirst = null, oldHead = null;
        if (indexed == IndexedChain.Skipping) skipping = fixture.ReorgToSkippingChainB();
        else if (indexed == IndexedChain.Shorter)
        {
            oldFirst = fixture.Import(fixture.Chain.AnchorRoot, slot: 1, 0xb1);
            oldHead = fixture.Import(oldFirst.Root, slot: 2, 0xb2, [fixture.Chain.Vote(1, oldFirst.Root)]);
            Assert.That(fixture.ComputeHead(), Is.EqualTo(oldHead.Root));
        }

        CanonicalReorgFixture restarted = fixture.Restart();
        Hash256 expectedHead = restarted.Chain.AnchorRoot;
        Hash256?[] expected = new Hash256?[indexed == IndexedChain.Skipping ? 5 : 4];
        expected[0] = expectedHead;
        if (replayThrough > 0)
        {
            UnsignedChain.ChainBlock first = restarted.Import(expectedHead, slot: 1, indexed == IndexedChain.Original ? (byte)0xa1 : (byte)0xb1);
            if (indexed == IndexedChain.Original) Assert.That(first.Root, Is.EqualTo(chainA[0].Root), "fixture: the replay re-imports the same block");
            expectedHead = indexed == IndexedChain.Shorter ? oldFirst!.Root : first.Root;
            expected[1] = expectedHead;
            if (replayThrough > 1)
            {
                UnsignedChain.ChainBlock head = indexed == IndexedChain.Skipping
                    ? restarted.Import(first.Root, slot: 4, 0xb4, [restarted.Chain.Vote(1, first.Root), restarted.Chain.Vote(3, first.Root)])
                    : restarted.Import(oldFirst!.Root, slot: 2, 0xb2, [restarted.Chain.Vote(1, oldFirst.Root)]);
                if (indexed == IndexedChain.Skipping) Assert.That(head.Root, Is.EqualTo(skipping!.BHead.Root), "fixture: the replay re-imports the same block");
                expectedHead = indexed == IndexedChain.Shorter ? oldHead!.Root : head.Root;
                expected[replayThrough] = expectedHead;
            }
        }

        Assert.That(restarted.ComputeHead(), Is.EqualTo(expectedHead));
        Assert.That(restarted.CanonicalRoots(through: (ulong)expected.Length - 1), Is.EqualTo(expected));
    }
}

/// <summary>A column store whose write batches can be made to fail at commit without applying anything, as a crash before the commit would.</summary>
internal sealed class FailableCommitColumnsDb(TestMemColumnsDb<BeaconChainDbColumns> inner) : IColumnsDb<BeaconChainDbColumns>
{
    public TestMemColumnsDb<BeaconChainDbColumns> Inner => inner;

    /// <summary>When set, the next write batch that stages a canonical index change is discarded and its commit throws.</summary>
    public bool FailNextCommit { get; set; }

    public IDb GetColumnDb(BeaconChainDbColumns key) => inner.GetColumnDb(key);

    public IEnumerable<BeaconChainDbColumns> ColumnKeys => inner.ColumnKeys;

    public IColumnsWriteBatch<BeaconChainDbColumns> StartWriteBatch() => new Batch(this, inner.StartWriteBatch());

    public IColumnDbSnapshot<BeaconChainDbColumns> CreateSnapshot() => inner.CreateSnapshot();

    public void Flush(bool onlyWal) => inner.Flush(onlyWal);

    public void Dispose() => inner.Dispose();

    private sealed class Batch(FailableCommitColumnsDb owner, IColumnsWriteBatch<BeaconChainDbColumns> real) : IColumnsWriteBatch<BeaconChainDbColumns>
    {
        private readonly MemColumnsDb<BeaconChainDbColumns> _discarded = new();
        private bool _failing;

        public IWriteBatch GetColumnBatch(BeaconChainDbColumns key)
        {
            if (key == BeaconChainDbColumns.BlockIndex && owner.FailNextCommit)
            {
                owner.FailNextCommit = false;
                _failing = true;
            }

            return _failing ? _discarded.GetColumnDb(key).StartWriteBatch() : real.GetColumnBatch(key);
        }

        public void Clear() => real.Clear();

        public void Dispose()
        {
            if (_failing)
            {
                real.Clear();
                real.Dispose();
                throw new IOException("the write batch failed to commit");
            }

            real.Dispose();
        }
    }
}

/// <summary>An importer over <see cref="UnsignedChain"/> whose canonical index lives in a store the test can read and count.</summary>
internal sealed class CanonicalReorgFixture
{
    private readonly ITimestamper _time;

    private CanonicalReorgFixture(UnsignedChain chain, FailableCommitColumnsDb db, BeaconChainStore store, BlockImporter importer, ITimestamper time)
    {
        _time = time;
        Chain = chain;
        Db = db;
        Store = store;
        Importer = importer;
    }

    /// <summary>Chain B: <c>B1</c> at slot 1 and <c>BHead</c> at slot 4, which skips slots 2 and 3.</summary>
    public sealed record Reorg(UnsignedChain.ChainBlock B1, UnsignedChain.ChainBlock BHead);

    public UnsignedChain Chain { get; }

    public FailableCommitColumnsDb Db { get; }

    public BeaconChainStore Store { get; }

    public BlockImporter Importer { get; }

    public TestMemDb BlockIndex => (TestMemDb)Db.GetColumnDb(BeaconChainDbColumns.BlockIndex);

    public static CanonicalReorgFixture Create(ITimestamper? time = null)
    {
        UnsignedChain chain = UnsignedChain.Create();
        FailableCommitColumnsDb db = new(new TestMemColumnsDb<BeaconChainDbColumns>());
        BeaconChainStore store = new(db, chain.Spec);
        store.SetAnchor(chain.AnchorRoot, 0);
        time ??= Timestamper.Default;
        return new CanonicalReorgFixture(chain, db, store, CreateImporter(chain, store, time), time);
    }

    /// <summary>A new importer over the same store and anchor, as a restart builds one; blocks must be imported again.</summary>
    public CanonicalReorgFixture Restart() => new(Chain, Db, Store, CreateImporter(Chain, Store, _time), _time);

    private static BlockImporter CreateImporter(UnsignedChain chain, BeaconChainStore store, ITimestamper time)
    {
        ImportableBlobBlock anchor = chain.Anchor;
        BlockImporter importer = new(
            chain.Spec,
            store,
            anchor.Pubkeys,
            new ValidEngine(),
            new BeaconChainConfig(),
            LimboLogs.Instance,
            new CustodySamplingAvailability(new NoCustody(), new DataColumnPoolSource(new DataColumnSidecarPool()), anchor.ClockAtEpoch(0)),
            static (_, _) => false,
            new SlotClock(chain.Spec, time),
            // A copy: the importer advances its anchor state in place, and the chain still builds on the original.
            new ForkedBeaconState.OfFulu(anchor.AnchorState.Clone()),
            new ForkedSignedBeaconBlock.OfFulu(anchor.AnchorBlock),
            anchor.AnchorRoot);
        // Past every block's slot, so none is timely and proposer boost never decides the head.
        importer.OnSlotTick(8);
        return importer;
    }

    public static byte[] SlotKey(ulong slot)
    {
        byte[] key = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(key, slot);
        return key;
    }

    public UnsignedChain.ChainBlock Import(Hash256 parentRoot, ulong slot, byte payloadHashByte, Attestation[]? attestations = null)
    {
        UnsignedChain.ChainBlock block = Chain.Extend(parentRoot, slot, payloadHashByte, attestations);
        Assert.That(Importer.Import(block.Block, block.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));
        return block;
    }

    public Hash256 ComputeHead() => Importer.ComputeHead().HeadRoot;

    /// <summary>Chain A, slots 1 to 3 on the anchor with no votes, made canonical.</summary>
    public UnsignedChain.ChainBlock[] ImportLongerChainA()
    {
        UnsignedChain.ChainBlock a1 = Import(Chain.AnchorRoot, slot: 1, 0xa1);
        UnsignedChain.ChainBlock a2 = Import(a1.Root, slot: 2, 0xa2);
        UnsignedChain.ChainBlock a3 = Import(a2.Root, slot: 3, 0xa3);
        Assert.That(ComputeHead(), Is.EqualTo(a3.Root));
        Assert.That(CanonicalRoots(through: 3), Is.EqualTo(new Hash256?[] { Chain.AnchorRoot, a1.Root, a2.Root, a3.Root }));
        return [a1, a2, a3];
    }

    /// <summary>Chain B over <see cref="ImportLongerChainA"/>: its slot-4 block carries two votes for it, so the head moves to it.</summary>
    public Reorg ReorgToSkippingChainB()
    {
        UnsignedChain.ChainBlock b1 = Import(Chain.AnchorRoot, slot: 1, 0xb1);
        UnsignedChain.ChainBlock b4 = Import(b1.Root, slot: 4, 0xb4, [Chain.Vote(1, b1.Root), Chain.Vote(3, b1.Root)]);
        Assert.That(ComputeHead(), Is.EqualTo(b4.Root), "two votes put chain B ahead of the unvoted chain A");
        return new Reorg(b1, b4);
    }

    /// <summary>The canonical root of every slot from 0 through <paramref name="through"/>, <c>null</c> where the index holds none.</summary>
    public Hash256?[] CanonicalRoots(ulong through) =>
        [.. Enumerable.Range(0, (int)through + 1).Select(slot => Store.TryGetCanonicalRoot((ulong)slot, out Hash256? root) ? root : null)];

    private sealed class NoCustody : INodeColumnCustodySource
    {
        public NodeColumnCustody? Current => null;
    }

    private sealed class ValidEngine : IEngineDriver
    {
        public SignedBeaconBlock? CurrentBlock { get; set; }

        public bool HasAnsweredNewPayload => false;

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash) =>
            Task.FromResult(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = headExecHash });

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }
}
