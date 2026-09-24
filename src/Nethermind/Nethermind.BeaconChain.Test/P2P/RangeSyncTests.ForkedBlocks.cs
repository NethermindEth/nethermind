// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P;

public partial class RangeSyncTests
{
    /// <summary>
    /// A Gloas block's commitments sit in its bid and its sidecars have the Gloas shape, so the Fulu column
    /// request of a batch that crosses the fork must cover only the Fulu blocks: stretching it over Gloas slots
    /// asks for Fulu sidecars that cannot exist, and reading a Gloas block as blob-free would call its data available.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Batch_crossing_the_fork_fetches_fulu_columns_only_for_its_fulu_blocks(CancellationToken token)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        await using BeaconDiscovery discovery = new(new BeaconChainConfig { Discv5Port = 0 }, chain.Spec, store, new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
        discovery.CreateDiscv5Services(IPAddress.Loopback);

        ulong fuluSlot = chain.Block.Message!.Slot;
        ForkedSignedBeaconBlock gloasChild = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(fuluSlot + 1, chain.BlockRoot));
        List<(ulong StartSlot, ulong Count)> columnWindows = [];
        StubPeer peer = new(
            "peer",
            headSlot: gloasChild.Slot,
            (startSlot, count) => [new ForkedSignedBeaconBlock.OfFulu(chain.Block), gloasChild],
            (startSlot, count, columns) =>
            {
                columnWindows.Add((startSlot, count));
                return [.. columns.Select(c => chain.Columns[(int)c])];
            });
        DataColumnSidecarPool sidecarPool = new();
        RangeSync sync = new(new StubPool(peer), LimboLogs.Instance, sidecarPool, chain.Spec, discovery);

        List<ForkedSignedBeaconBlock> yielded = [];
        await foreach (ForkedSignedBeaconBlock block in sync.Run(chain.AnchorRoot, chain.AnchorBlock.Message!.Slot, () => gloasChild.Slot, token))
        {
            yielded.Add(block);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(yielded.Select(static b => b.GetType()), Is.EqualTo(new[] { typeof(ForkedSignedBeaconBlock.OfFulu), typeof(ForkedSignedBeaconBlock.OfGloas) }), "both blocks are yielded, linked across the fork");
            Assert.That(columnWindows, Is.EqualTo(new[] { (fuluSlot, 1UL) }), "the Fulu column window covers only the Fulu block");
            Assert.That(peer.Failures, Is.Zero, "the Gloas block costs the peer nothing");
        }
    }
}
