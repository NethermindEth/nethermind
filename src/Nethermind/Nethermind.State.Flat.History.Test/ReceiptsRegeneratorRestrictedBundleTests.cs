// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.OverridableEnv;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class ReceiptsRegeneratorRestrictedBundleTests
{
    [Test]
    public void TryRegenerate_WhenReplayTouchesAnAddressARestrictedBundleRefuses_ReturnsFalse_NotAWrongResult()
    {
        TestSpecProvider specProvider = new(Byzantium.Instance);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        BlockHeader parent = Build.A.BlockHeader.WithNumber(4).TestObject;
        blockFinder.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(parent);

        IEthereumEcdsa ecdsa = Substitute.For<IEthereumEcdsa>();
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();

        StateUnavailableException sliceRefusal = new(
            "Historical state for block 4 is unavailable for 0x0000000000000000000000000000000000000def - it is below the general retention floor and not covered by any retained slice.");
        MissingTrieNodeException restrictedBundleRefusal = new(
            "Historical state for block 4 is unavailable", null, TreePath.Empty, Keccak.Zero, sliceRefusal);

        IBlockProcessor blockProcessor = Substitute.For<IBlockProcessor>();
        blockProcessor
            .ProcessOne(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<IReleaseSpec>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw restrictedBundleRefusal);

        IShareableOverridableEnvSource<ReceiptsRegenerationEnv> envSource = Substitute.For<IShareableOverridableEnvSource<ReceiptsRegenerationEnv>>();
        envSource
            .BuildAndOverride(Arg.Any<BlockHeader>(), Arg.Any<Dictionary<Address, AccountOverride>>(), Arg.Any<BlockOverride>())
            .Returns(new Scope<ReceiptsRegenerationEnv>(new ReceiptsRegenerationEnv(blockProcessor), Substitute.For<IDisposable>()));

        ReceiptsRegenerator regenerator = new(envSource, blockFinder, specProvider, ecdsa, poSSwitcher, LimboLogs.Instance);
        Block block = Build.A.Block.WithNumber(5).WithParentHash(parent.Hash!).TestObject;

        bool regenerated = regenerator.TryRegenerate(block, out TxReceipt[]? receipts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(regenerated, Is.False,
                "regenerating a block below the general floor with slices configured replays the whole block, touching non-sliced addresses whose state is gone - this is structurally impossible, not a transient failure, so it must refuse cleanly");
            Assert.That(receipts, Is.Null, "a refusal must never hand back a partial or wrong receipt set");
        }
    }
}
