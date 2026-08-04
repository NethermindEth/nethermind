// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
internal class SignTransactionManagerTests
{
    // window = MergeSignRange(15) * MinePeriod(2) * MaxSignableBlockPeriods(2) = 60s.
    [TestCase(0L, true)]
    [TestCase(60L, true)]
    [TestCase(61L, false)]
    [TestCase(86_400L, false)]
    public void OnBlockAddedToMain_SignsOnlyRecentHeadBlocks(long secondsBehind, bool shouldSign)
    {
        IXdcReleaseSpec spec = Substitute.For<IXdcReleaseSpec>();
        spec.MergeSignRange.Returns(15UL);
        spec.MinePeriod.Returns(2UL);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        ManualTimestamper timestamper = new();

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithNumber(spec.MergeSignRange)
            .WithTimestamp((ulong)(timestamper.UnixTime.SecondsLong - secondsBehind))
            .WithExtraConsensusData(new ExtraFieldsV2(1, new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 0, 0), null, 0)))
            .TestObject;
        Block block = new(header);

        ISigner signer = Substitute.For<ISigner>();
        signer.Address.Returns(TestItem.AddressA);
        signer.TrySign(Arg.Any<Transaction>()).Returns(true);

        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(AcceptTxResult.Accepted);

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.WasProcessed(block.Number, block.Hash!).Returns(true);
        blockTree.FindBestSuggestedHeader().Returns(header); // bestSuggested == head => not syncing
        blockTree.Head.Returns(block);

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager.GetSnapshotByBlockNumber(Arg.Any<ulong>(), Arg.Any<IXdcReleaseSpec>())
            .Returns(new Snapshot(block.Number, TestItem.KeccakA, [TestItem.AddressA]));

        SignTransactionManager manager = new(
            new Lazy<ISigner>(() => signer),
            new Lazy<ITxPool>(() => txPool),
            blockTree, snapshotManager, specProvider, timestamper, LimboLogs.Instance);
        manager.Start();

        blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));

        txPool.Received(shouldSign ? 1 : 0).SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
    }
}
