// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
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
    public void DeleteCanonicalRoot_removes_only_the_given_slot_and_reports_whether_it_held_one()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        store.SetCanonicalRoot(7, Keccak.Compute("seven"));
        store.SetCanonicalRoot(8, Keccak.Compute("eight"));

        bool removed = store.DeleteCanonicalRoot(7);
        bool removedAgain = store.DeleteCanonicalRoot(7);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.True);
            Assert.That(removedAgain, Is.False, "an empty slot has nothing to remove");
            Assert.That(store.TryGetCanonicalRoot(7, out _), Is.False);
            Assert.That(store.TryGetCanonicalRoot(8, out Hash256? eight) && eight == Keccak.Compute("eight"), Is.True);
        });
    }
}

/// <summary>An importer over <see cref="UnsignedChain"/> whose canonical index lives in a store the test can read and count.</summary>
internal sealed class CanonicalReorgFixture
{
    private CanonicalReorgFixture(UnsignedChain chain, TestMemColumnsDb<BeaconChainDbColumns> db, BeaconChainStore store, BlockImporter importer)
    {
        Chain = chain;
        Db = db;
        Store = store;
        Importer = importer;
    }

    /// <summary>Chain B: <c>B1</c> at slot 1 and <c>BHead</c> at slot 4, which skips slots 2 and 3.</summary>
    public sealed record Reorg(UnsignedChain.ChainBlock B1, UnsignedChain.ChainBlock BHead);

    public UnsignedChain Chain { get; }

    public TestMemColumnsDb<BeaconChainDbColumns> Db { get; }

    public BeaconChainStore Store { get; }

    public BlockImporter Importer { get; }

    public TestMemDb BlockIndex => (TestMemDb)Db.GetColumnDb(BeaconChainDbColumns.BlockIndex);

    public static CanonicalReorgFixture Create()
    {
        UnsignedChain chain = UnsignedChain.Create();
        TestMemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db, chain.Spec);
        store.SetAnchor(chain.AnchorRoot, 0);
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
            new SlotClock(chain.Spec, Timestamper.Default),
            // A copy: the importer advances its anchor state in place, and the chain still builds on the original.
            anchor.AnchorState.Clone(),
            anchor.AnchorBlock,
            anchor.AnchorRoot);
        // Past every block's slot, so none is timely and proposer boost never decides the head.
        importer.OnSlotTick(8);
        return new CanonicalReorgFixture(chain, db, store, importer);
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
