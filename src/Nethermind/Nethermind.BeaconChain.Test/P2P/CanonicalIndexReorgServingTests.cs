// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.Storage;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// The by-range servers read the canonical index the importer keeps. After a reorg to a chain that skips slots, a server
/// that still named the orphans there would answer with blocks that do not link (the requester penalizes us for it) and
/// with the orphans' columns.
/// </summary>
public class CanonicalIndexReorgServingTests
{
    [Test]
    public async Task Blocks_by_range_after_a_reorg_serves_only_the_new_chain()
    {
        CanonicalReorgFixture fixture = CanonicalReorgFixture.Create();
        fixture.ImportLongerChainA();
        CanonicalReorgFixture.Reorg reorg = fixture.ReorgToSkippingChainB();
        BeaconBlocksByRangeProtocolV2 protocol = new(fixture.Chain.Spec, fixture.Store);

        IReadOnlyList<ForkedSignedBeaconBlock> served = await ServeAsync(
            protocol, (channel, context) => protocol.DialAsync(channel, context, new BeaconBlocksByRangeDial(new BeaconBlocksByRangeRequest { StartSlot = 1, Count = 4, Step = 1 })));

        Assert.That(served.Select(static b => b.ComputeMessageRoot()), Is.EqualTo(new[] { reorg.B1.Root, reorg.BHead.Root }),
            "slots 2 and 3 are empty on chain B; serving chain A's blocks there breaks the parent links");
    }

    [Test]
    public async Task Column_by_range_after_a_reorg_does_not_serve_the_orphans_columns()
    {
        const ulong column = 5;
        CanonicalReorgFixture fixture = CanonicalReorgFixture.Create();
        UnsignedChain.ChainBlock[] chainA = fixture.ImportLongerChainA();
        CanonicalReorgFixture.Reorg reorg = fixture.ReorgToSkippingChainB();
        DataColumnSidecarPool pool = new();
        pool.Add(reorg.B1.Root, 1, DataColumnSidecarTestFixture.BuildValidSidecar(column, slot: 1, blobCount: 1, seed: 0x31));
        pool.Add(chainA[1].Root, 2, DataColumnSidecarTestFixture.BuildValidSidecar(column, slot: 2, blobCount: 1, seed: 0x32));
        pool.Add(chainA[2].Root, 3, DataColumnSidecarTestFixture.BuildValidSidecar(column, slot: 3, blobCount: 1, seed: 0x33));
        DataColumnSidecarsByRangeProtocol protocol = new(fixture.Chain.Spec, pool, fixture.Store);

        ForkedDataColumnSidecars served = await ServeAsync(protocol, (channel, context) => protocol.DialAsync(
            channel, context, new(new DataColumnSidecarsByRangeRequest { StartSlot = 1, Count = 4, Columns = [column] }, Gloas: false)));

        Assert.That(served.Fulu.Select(static s => s.SignedBlockHeader!.Message!.Slot), Is.EqualTo(new[] { 1UL }),
            "the columns held for chain A's slot 2 and 3 blocks belong to orphans");
    }

    [Test]
    public async Task Blocks_by_range_refuses_only_a_start_below_the_earliest_stored_block([Values] bool anchorAdvanced)
    {
        const ulong anchorSlot = 13_410_304;
        BeaconChainSpec spec = BeaconChainSpec.Mainnet;
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), spec);
        (SignedBeaconBlock anchor, Hash256 anchorRoot, SignedBeaconBlock[] blocks) = TestChain.BuildLinkedChain(anchorSlot, anchorSlot + 1, anchorSlot + 2);
        TestChain.Persist(store, anchor, anchorRoot, blocks);
        if (anchorAdvanced) store.SetAnchor(SszRoots.HashTreeRoot(blocks[^1].Message!), anchorSlot + 2);
        BeaconBlocksByRangeProtocolV2 protocol = new(spec, store);

        IReadOnlyList<ForkedSignedBeaconBlock> served = await ServeAsync(
            protocol, (channel, context) => protocol.DialAsync(channel, context, new BeaconBlocksByRangeDial(new BeaconBlocksByRangeRequest { StartSlot = anchorSlot, Count = 3, Step = 1 })));
        Eth2ReqRespException? refused = Assert.ThrowsAsync<Eth2ReqRespException>(() => ServeAsync(
            protocol, (channel, context) => protocol.DialAsync(channel, context, new BeaconBlocksByRangeDial(new BeaconBlocksByRangeRequest { StartSlot = anchorSlot - 1, Count = 3, Step = 1 }))));

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(served.Select(static b => b.Slot), Is.EqualTo(new[] { anchorSlot, anchorSlot + 1, anchorSlot + 2 }), "finality moving the anchor keeps the canonical blocks below it servable");
        Assert.That(refused!.ResponseCode, Is.EqualTo(ReqRespFraming.ResponseCode.ResourceUnavailable), "an empty reply would claim the slots below the earliest block are empty");
    }

    [Test]
    public async Task Blocks_by_range_respects_the_verified_backfill_floor([Values] bool belowFloor)
    {
        const ulong start = 13_410_304;
        BeaconChainSpec spec = BeaconChainSpec.Mainnet;
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), spec);
        (SignedBeaconBlock anchor, Hash256 anchorRoot, SignedBeaconBlock[] blocks) = TestChain.BuildLinkedChain(start, start + 1, start + 2);
        TestChain.Persist(store, anchor, anchorRoot, blocks);
        store.SetAnchor(SszRoots.HashTreeRoot(blocks[^1].Message!), start + 2);
        store.PutMetadata(BeaconChainMetadataKeys.EarliestBlockSlot, []);
        store.BackfilledBlockFloor = start + 1;
        BeaconBlocksByRangeProtocolV2 protocol = new(spec, store);
        BeaconBlocksByRangeDial request = new(new BeaconBlocksByRangeRequest { StartSlot = belowFloor ? start : start + 1, Count = 2, Step = 1 });

        if (belowFloor)
        {
            Eth2ReqRespException? refused = Assert.ThrowsAsync<Eth2ReqRespException>(() => ServeAsync(
                protocol, (channel, context) => protocol.DialAsync(channel, context, request)));
            Assert.That(refused!.ResponseCode, Is.EqualTo(ReqRespFraming.ResponseCode.ResourceUnavailable));
        }
        else
        {
            IReadOnlyList<ForkedSignedBeaconBlock> served = await ServeAsync(
                protocol, (channel, context) => protocol.DialAsync(channel, context, request));
            Assert.That(served.Select(static b => b.Slot), Is.EqualTo(new[] { start + 1, start + 2 }));
        }
    }

    [Test]
    public async Task Blocks_by_range_serves_the_last_slot_without_wrapping([Values(1UL, ulong.MaxValue)] ulong count)
    {
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(ulong.MaxValue));
        Hash256 root = block.ComputeMessageRoot();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);
        store.PutForkedBlock(root, block);
        store.SetCanonicalRoot(ulong.MaxValue, root);
        store.SetAnchor(root, ulong.MaxValue);
        BeaconBlocksByRangeProtocolV2 protocol = new(Sepolia, store);

        List<ResponseChunk> served = await ServeAsync(protocol, async (channel, _) =>
        {
            ChannelStreamAdapter input = new(channel);
            await ReqRespFraming.WriteRequestAsync(input, BeaconBlocksByRangeRequest.Encode(
                new BeaconBlocksByRangeRequest { StartSlot = ulong.MaxValue, Count = count, Step = 1 }), default);
            await channel.WriteEofAsync();
            List<ResponseChunk> chunks = [];
            while (await ReqRespFraming.ReadResponseChunkAsync(input, 4, ReqRespFraming.MaxPayloadSize, default) is { } chunk) chunks.Add(chunk);
            return chunks;
        });

        Assert.That(served, Has.Count.EqualTo(1));
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(served[0].Result, Is.EqualTo(ReqRespFraming.ResponseCode.Success));
        Assert.That(served[0].Payload, Is.EqualTo(SignedBeaconBlockCodec.Encode(block, Sepolia)));
    }

    internal static async Task<T> ServeAsync<T>(ISessionListenerProtocol protocol, System.Func<IChannel, ISessionContext, Task<T>> dial)
    {
        ISessionContext context = ReqRespTestChannel.Context();
        Channel channel = new();
        Task listen = ReqRespTestChannel.ListenThenCloseAsync(protocol, channel.Reverse, context);
        T result = await dial(channel, context);
        await listen;
        return result;
    }

}
