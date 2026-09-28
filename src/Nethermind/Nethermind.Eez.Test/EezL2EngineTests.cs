// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Follower;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezL2EngineTests
{
    private static readonly Block Head = Build.A.Block.WithNumber(7).TestObject;
    private static readonly Hash256 Safe = Keccak.Compute("safe");
    private static readonly Hash256 Finalized = Keccak.Compute("finalized");

    private IEngineRpcModule _engine = null!;
    private IBlockTree _blockTree = null!;
    private EezL2Engine _l2 = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = Substitute.For<IEngineRpcModule>();
        _engine.engine_forkchoiceUpdatedV3(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>())
            .Returns(ResultWrapper<ForkchoiceUpdatedV1Result>.Success(new ForkchoiceUpdatedV1Result { PayloadStatus = new PayloadStatusV1 { Status = PayloadStatus.Valid } }));
        _blockTree = Substitute.For<IBlockTree>();
        _l2 = new EezL2Engine(_engine, _blockTree, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _l2.Dispose();

    [Test]
    public async Task EnsureSoleDriver_OnlyTheFollowersForkchoice_Passes()
    {
        await _l2.UpdateForkchoice(Head.Hash!, Safe, Finalized);
        Applied(Head, Safe, Finalized);

        Assert.DoesNotThrow(_l2.EnsureSoleDriver, "the forkchoice the follower sent is the one applied");
    }

    [Test]
    public async Task EnsureSoleDriver_AnotherClientMovedTheHead_Throws()
    {
        await _l2.UpdateForkchoice(Head.Hash!, Safe, Finalized);
        Applied(Build.A.Block.WithNumber(8).TestObject, Safe, Finalized);

        Assert.Throws<EezFollowerException>(_l2.EnsureSoleDriver, "a consensus client on the engine endpoint moved the head");
    }

    [Test]
    public void EnsureSoleDriver_BeforeTheFollowerSentAnything_Passes()
    {
        Applied(Head, Safe, Finalized);

        Assert.DoesNotThrow(_l2.EnsureSoleDriver, "the node's own forkchoice before the follower starts is not a foreign one");
    }

    private void Applied(Block head, Hash256 safe, Hash256 finalized)
    {
        _blockTree.SafeHash.Returns(safe);
        _blockTree.FinalizedHash.Returns(finalized);
        _blockTree.OnForkChoiceUpdated += Raise.Event<System.EventHandler<IBlockTree.ForkChoiceUpdateEventArgs>>(_blockTree,
            new IBlockTree.ForkChoiceUpdateEventArgs(head, head.Number, head.Number));
    }
}
