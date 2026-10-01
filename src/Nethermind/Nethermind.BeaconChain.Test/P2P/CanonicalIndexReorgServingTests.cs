// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.Storage;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Libp2p.Core;
using NSubstitute;
using NUnit.Framework;

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
            protocol, (channel, context) => protocol.DialAsync(channel, context, new BeaconBlocksByRangeRequest { StartSlot = 1, Count = 4, Step = 1 }));

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

    internal static async Task<T> ServeAsync<T>(ISessionListenerProtocol protocol, System.Func<IChannel, ISessionContext, Task<T>> dial)
    {
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new Nethermind.Libp2p.Core.State());
        Channel channel = new();
        Task listen = ListenThenCloseAsync(protocol, channel.Reverse, context);
        T result = await dial(channel, context);
        await listen;
        return result;
    }

    // The libp2p host closes the response stream once the handler returns; the dial side reads until then.
    private static async Task ListenThenCloseAsync(ISessionListenerProtocol protocol, IChannel channel, ISessionContext context)
    {
        await protocol.ListenAsync(channel, context);
        await channel.WriteEofAsync();
    }
}
