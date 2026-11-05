// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    [TestCaseSource(nameof(ForkchoiceOutcomes))]
    public void UpdateForkchoice_NodeDoesNotApplyIt_ThrowsByWhetherItClears(ResultWrapper<ForkchoiceUpdatedV1Result> result, Type expected)
    {
        _engine.engine_forkchoiceUpdatedV3(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>()).Returns(result);

        Assert.That(() => _l2.UpdateForkchoice(Head.Hash!, Safe, Finalized), Throws.TypeOf(expected),
            "only an invalid forkchoice stops the follower; one the node has not processed yet is asked again");
    }

    [TestCaseSource(nameof(PayloadOutcomes))]
    public void Insert_NodeDoesNotValidateIt_ThrowsByWhetherItClears(ResultWrapper<PayloadStatusV1> result, Type expected)
    {
        _engine.engine_newPayloadV4(Arg.Any<ExecutionPayloadV3>(), Arg.Any<Hash256?[]>(), Arg.Any<Hash256?>(), Arg.Any<byte[][]?>()).Returns(result);

        Assert.That(() => _l2.Insert(Head), Throws.TypeOf(expected), "only an invalid block stops the follower; a busy node is asked again");
    }

    private static TestCaseData[] ForkchoiceOutcomes() =>
    [
        new TestCaseData(ForkchoiceUpdatedV1Result.Syncing, typeof(EezEngineUnavailableException))
            { TestName = "Syncing" },
        new TestCaseData(ForkchoiceUpdatedV1Result.Invalid(Keccak.Zero), typeof(EezFollowerException))
            { TestName = "Invalid" },
        new TestCaseData(ResultWrapper<ForkchoiceUpdatedV1Result>.Fail("Timed out", ErrorCodes.Timeout), typeof(EezEngineUnavailableException))
            { TestName = "LockTimedOut" },
        new TestCaseData(ResultWrapper<ForkchoiceUpdatedV1Result>.Fail("not an ancestor", MergeErrorCodes.InvalidForkchoiceState), typeof(EezFollowerException))
            { TestName = "InvalidForkchoiceState" },
    ];

    private static TestCaseData[] PayloadOutcomes() =>
    [
        new TestCaseData(ResultWrapper<PayloadStatusV1>.Success(PayloadStatusV1.Syncing), typeof(EezEngineUnavailableException)) { TestName = "Syncing" },
        new TestCaseData(ResultWrapper<PayloadStatusV1>.Success(PayloadStatusV1.Accepted), typeof(EezEngineUnavailableException)) { TestName = "Accepted" },
        new TestCaseData(ResultWrapper<PayloadStatusV1>.Success(PayloadStatusV1.Invalid(null, "bad state root")), typeof(EezFollowerException)) { TestName = "Invalid" },
        new TestCaseData(ResultWrapper<PayloadStatusV1>.Fail("Timed out", ErrorCodes.Timeout), typeof(EezEngineUnavailableException)) { TestName = "LockTimedOut" },
        new TestCaseData(ResultWrapper<PayloadStatusV1>.Fail("malformed", ErrorCodes.InvalidParams), typeof(EezFollowerException)) { TestName = "InvalidParams" },
    ];

    private void Applied(Block head, Hash256 safe, Hash256 finalized)
    {
        _blockTree.SafeHash.Returns(safe);
        _blockTree.FinalizedHash.Returns(finalized);
        _blockTree.OnForkChoiceUpdated += Raise.Event<EventHandler<IBlockTree.ForkChoiceUpdateEventArgs>>(_blockTree,
            new IBlockTree.ForkChoiceUpdateEventArgs(head, head.Number, head.Number));
    }
}
