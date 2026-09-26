// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Db;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class SnapStateServerTests
{
    [Test]
    public async Task TestGetAccountRange_AtSnapServingDepthBoundary_IsServable()
    {
        const int chainLength = 200;

        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<IFlatDbConfig>(new FlatDbConfig())
            .AddSingleton<ISyncConfig>(new SyncConfig { SnapServingEnabled = true }));

        await chain.BuildSomeBlocks(chainLength);

        ISyncConfig syncConfig = chain.Container.Resolve<ISyncConfig>();
        ISnapStateServer server = chain.WorldStateManager.SnapStateServer!;

        ulong depth = syncConfig.SnapServingMaxDepth - 1;
        ulong boundaryNumber = chain.BlockTree.Head!.Number - depth;
        BlockHeader boundary = chain.BlockTree.FindHeader(boundaryNumber, BlockTreeLookupOptions.None)!;

        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) = server.GetAccountRanges(
            boundary.StateRoot!, Keccak.Zero, Keccak.MaxValue, 4000, CancellationToken.None);

        using (accounts)
        using (proofs)
        {
            Assert.That(accounts, Is.Not.Empty,
                $"state root of block {boundaryNumber} (head-{depth}) was not servable");
        }
    }
}
