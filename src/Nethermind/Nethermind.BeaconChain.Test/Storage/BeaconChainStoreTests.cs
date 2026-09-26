// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Storage;

public class BeaconChainStoreTests
{
    [Test]
    public void Round_trips_blocks_canonical_index_chunked_states_metadata_and_anchor()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        Hash256 blockRoot = new(Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111"));
        Hash256 missingRoot = new(Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222"));

        SignedBeaconBlock block = CreateMinimalBlock(12_345_678);
        store.PutBlock(blockRoot, block);

        store.SetCanonicalRoot(12_345_678, blockRoot);

        // ~10 MB forces three 4 MB chunks; random data also exercises barely-compressible chunks.
        byte[] stateSsz = new byte[10 * 1024 * 1024 + 17];
        new Random(42).NextBytes(stateSsz);
        store.PutState(blockRoot, stateSsz);

        store.PutMetadata("probe", [1]);
        store.SetAnchor(blockRoot, 12_345_678);

        Assert.Multiple(() =>
        {
            Assert.That(store.TryGetBlock(blockRoot, out SignedBeaconBlock? readBlock), Is.True);
            Assert.That(SignedBeaconBlock.Encode(readBlock!), Is.EqualTo(SignedBeaconBlock.Encode(block)));
            Assert.That(store.TryGetBlock(missingRoot, out _), Is.False);
            Assert.That(store.HasBlock(blockRoot), Is.True);
            Assert.That(store.HasBlock(missingRoot), Is.False);

            Assert.That(store.TryGetCanonicalRoot(12_345_678, out Hash256? canonicalRoot), Is.True);
            Assert.That(canonicalRoot, Is.EqualTo(blockRoot));
            Assert.That(store.TryGetCanonicalRoot(1, out _), Is.False);

            Assert.That(store.TryGetState(blockRoot, out byte[]? readState), Is.True);
            Assert.That(readState, Is.EqualTo(stateSsz));
            Assert.That(store.TryGetState(missingRoot, out _), Is.False);

            Assert.That(store.GetMetadata("probe"), Is.EqualTo(new byte[] { 1 }));
            Assert.That(store.GetMetadata("missing"), Is.Null);

            Assert.That(store.TryGetAnchor(out Hash256? anchorRoot, out ulong anchorSlot), Is.True);
            Assert.That(anchorRoot, Is.EqualTo(blockRoot));
            Assert.That(anchorSlot, Is.EqualTo(12_345_678ul));
        });
    }

    [Test]
    public void An_unversioned_database_is_stamped_with_the_current_schema_version()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        Assert.That(store.TryGetSchemaVersion(out _), Is.False);

        store.EnsureSchemaVersion();

        Assert.That(store.TryGetSchemaVersion(out uint version), Is.True);
        Assert.That(version, Is.EqualTo(BeaconChainStore.CurrentSchemaVersion));
    }

    [Test]
    public void A_database_from_a_newer_schema_version_is_refused_and_left_unstamped()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        uint newer = BeaconChainStore.CurrentSchemaVersion + 1;
        store.SetSchemaVersion(newer);

        Assert.That(store.EnsureSchemaVersion, Throws.InvalidOperationException.With.Message.Contains("delete the beaconChain database"));

        Assert.That(store.TryGetSchemaVersion(out uint version), Is.True);
        Assert.That(version, Is.EqualTo(newer), "a refused database must not be restamped as one this build can read");
    }

    private static readonly Hash256 BlockRoot = new(Bytes.FromHexString("0x3333333333333333333333333333333333333333333333333333333333333333"));

    // The last Fulu slot goes through the Fulu-typed adapters and the first Gloas slot does not, which pins the boundary on both sides.
    [Test]
    public void Fulu_and_gloas_blocks_read_back_in_the_shape_of_the_fork_their_slot_belongs_to()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);
        Hash256 fuluRoot = new(Bytes.FromHexString("0x4444444444444444444444444444444444444444444444444444444444444444"));
        SignedBeaconBlock fulu = CreateMinimalBlock(FirstGloasSlot - 1);
        // The children index must follow the block's parent_root, not the bid's parent_block_root, which an unvalidated block such as an anchor may set differently.
        SignedBeaconBlockGloas gloas = CreateMinimalGloasBlock(FirstGloasSlot, parentRoot: fuluRoot, bidParentRoot: Keccak.OfAnEmptyString);

        store.PutBlock(fuluRoot, fulu);
        store.PutForkedBlock(BlockRoot, new ForkedSignedBeaconBlock.OfGloas(gloas));

        Assert.That(store.TryGetForkedBlock(fuluRoot, out ForkedSignedBeaconBlock? readFulu), Is.True);
        Assert.That(store.TryGetForkedBlock(BlockRoot, out ForkedSignedBeaconBlock? readGloas), Is.True);
        Assert.That(store.TryGetBlock(fuluRoot, out SignedBeaconBlock? adapted), Is.True, "the Fulu-typed read still serves a Fulu block");
        Assert.That(readFulu, Is.TypeOf<ForkedSignedBeaconBlock.OfFulu>());
        Assert.That(readGloas, Is.TypeOf<ForkedSignedBeaconBlock.OfGloas>());
        bool hasChildren = store.TryGetChildren(fuluRoot, out Hash256[] children, out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SignedBeaconBlock.Encode(((ForkedSignedBeaconBlock.OfFulu)readFulu!).Block), Is.EqualTo(SignedBeaconBlock.Encode(fulu)));
            Assert.That(SignedBeaconBlockGloas.Encode(((ForkedSignedBeaconBlock.OfGloas)readGloas!).Block), Is.EqualTo(SignedBeaconBlockGloas.Encode(gloas)));
            Assert.That(SignedBeaconBlock.Encode(adapted!), Is.EqualTo(SignedBeaconBlock.Encode(fulu)));
            Assert.That(hasChildren, Is.True);
            Assert.That(children, Is.EqualTo(new[] { BlockRoot }));
        }
    }

    [Test]
    public void The_fulu_typed_read_refuses_a_gloas_block_by_name_rather_than_misreading_it()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);
        store.PutForkedBlock(BlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(FirstGloasSlot)));

        Assert.That(() => store.TryGetBlock(BlockRoot, out _),
            Throws.InvalidOperationException.With.Message.Contains(nameof(BeaconChainStore.TryGetForkedBlock)));
    }

    [Test]
    public void The_fulu_typed_write_refuses_a_block_at_a_gloas_slot_by_name_and_stores_nothing()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);

        Assert.That(() => store.PutBlock(BlockRoot, CreateMinimalBlock(FirstGloasSlot)),
            Throws.InvalidOperationException.With.Message.Contains(nameof(BeaconChainStore.PutForkedBlock)));
        Assert.That(store.HasBlock(BlockRoot), Is.False);
    }

    [Test]
    public void The_forked_write_refuses_a_shape_that_would_read_back_as_another_fork()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);

        Assert.That(() => store.PutForkedBlock(BlockRoot, new ForkedSignedBeaconBlock.OfFulu(CreateMinimalBlock(FirstGloasSlot))),
            Throws.TypeOf<BeaconStateException>());
        Assert.That(store.HasBlock(BlockRoot), Is.False);
    }

    // Without a spec no Gloas fork is known: any slot is the Fulu shape, as before the forked members existed.
    [Test]
    public void Without_a_spec_every_block_is_the_fulu_shape_and_a_gloas_block_is_refused()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        SignedBeaconBlock fulu = CreateMinimalBlock(FirstGloasSlot);

        store.PutBlock(BlockRoot, fulu);

        Assert.That(store.TryGetBlock(BlockRoot, out SignedBeaconBlock? read), Is.True);
        Assert.That(store.TryGetForkedBlock(BlockRoot, out ForkedSignedBeaconBlock? forked), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SignedBeaconBlock.Encode(read!), Is.EqualTo(SignedBeaconBlock.Encode(fulu)));
            Assert.That(forked, Is.TypeOf<ForkedSignedBeaconBlock.OfFulu>());
        }

        Assert.That(() => store.PutForkedBlock(BlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(FirstGloasSlot))),
            Throws.InvalidOperationException, "a Gloas block stored without a spec would read back as the Fulu shape");
    }
}
