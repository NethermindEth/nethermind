// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using Nethermind.Logging;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using Block = Nethermind.Core.Block;

namespace Nethermind.Eez.Test;

public class StreamingBlockExecutorTests
{
    private const string Fixture = "stateless-block-13";
    private BlockWitness _block = null!;
    private BlockHeaderData _parent;
    private StreamingBlockExecutor _executor = null!;

    [SetUp]
    public void SetUp()
    {
        using Witness witness = StatelessFixtures.ReadWitness(Fixture, "witness-13.json");
        byte[] encoded = StatelessFixtures.ReadBlock(Fixture, "block-13.rlp");
        Block block = Rlp.Decode<Block>(encoded)!;
        BlockHeader parent = Rlp.Decode<BlockHeader>(witness.Headers.Single())!;
        _parent = new BlockHeaderData(parent.Number, ByteString.CopyFrom(parent.Hash!.Bytes), ByteString.CopyFrom(parent.StateRoot!.Bytes));
        _block = new BlockWitness
        {
            Number = block.Number,
            Hash = ByteString.CopyFrom(block.Hash!.Bytes),
            ParentHash = ByteString.CopyFrom(block.ParentHash!.Bytes),
            Rlp = ByteString.CopyFrom(encoded),
            Witness = new ExecutionWitness(),
        };
        _block.Witness.State.Add(witness.State.Select(static item => ByteString.CopyFrom(item)));
        _block.Witness.Codes.Add(witness.Codes.Select(static item => ByteString.CopyFrom(item)));
        _block.Witness.Keys.Add(witness.Keys.Select(static item => ByteString.CopyFrom(item)));
        _block.Witness.Headers.Add(witness.Headers.Select(static item => ByteString.CopyFrom(item)));
        _executor = new StreamingBlockExecutor(StatelessFixtures.ReadSpecProvider(Fixture, "chain-config.json"), EezChainRules.Rollup0, LimboLogs.Instance);
        Assert.That((_parent.Number + 1, _parent.Hash), Is.EqualTo((_block.Number, _block.ParentHash)), "the independent witness carries the recorded parent");
    }

    [Test]
    public void Execute_RecordedBlock_AdvancesOnlyAfterExecutionCommits()
    {
        StreamingSession session = Session(_block);
        using SessionOperation operation = session.AdmitBlock(session.Binding, 7, _block);
        ExecutedStreamingBlock executed = _executor.Execute(operation, CancellationToken.None);
        Assert.That(session.Ready(session.Binding, 8).Ready.ValidatedThrough, Is.EqualTo(_parent.Number), "execution alone does not acknowledge progress");
        ServerFrame validated = session.CommitBlock(operation, executed);
        Assert.That((validated.RequestId, validated.Validated.Number, validated.Validated.Hash, validated.Validated.PostStateRoot),
            Is.EqualTo((7UL, 13UL, Hash("0x16b64a78e9b3e0d533cafe81f9121735f6a2c8122c69b0bb5994ee75fe7bface"),
                Hash("0xf09d8f7da5bc5036f8dd9536c953e2212390a46fb3e553ece2b7d419131537b1"))),
            "acknowledgement contains independently recorded execution values");
    }

    [TestCase("number", TestName = "Execute_FalseNumber_IsRejected")]
    [TestCase("hash", TestName = "Execute_FalseHash_IsRejected")]
    [TestCase("parent", TestName = "Execute_FalseParent_IsRejected")]
    [TestCase("root", TestName = "Execute_FalseAnchorRoot_IsRejected")]
    [TestCase("rlp", TestName = "Execute_InvalidRlp_IsRejected")]
    [TestCase("witness", TestName = "Execute_MissingState_IsRejected")]
    [TestCase("partialWitness", TestName = "Execute_MissingTrieChild_IsRejected")]
    [TestCase("header", TestName = "Execute_EmptyHeader_IsRejected")]
    [TestCase("linkage", TestName = "Execute_NoncontiguousHeaders_IsRejected")]
    [TestCase("trailing", TestName = "Execute_TrailingHeaderBytes_IsRejected")]
    public void Execute_UnboundInput_RejectsWithoutProgress(string mutation)
    {
        BlockWitness block = _block.Clone();
        switch (mutation)
        {
            case "number": block.Number++; break;
            case "hash": block.Hash = ByteString.CopyFrom(new byte[32]); break;
            case "parent": block.ParentHash = ByteString.CopyFrom(new byte[32]); break;
            case "rlp": block.Rlp = ByteString.CopyFrom(new byte[] { 0xc0 }); break;
            case "witness": block.Witness.State.Clear(); break;
            case "partialWitness":
                for (int i = block.Witness.State.Count - 1; i >= 0; i--)
                    if (!Keccak.Compute(block.Witness.State[i].Span).Bytes.SequenceEqual(_parent.StateRoot.Span)) block.Witness.State.RemoveAt(i);
                Assert.That(block.Witness.State, Has.Count.EqualTo(1), "precondition: the root is present but its child proofs are missing");
                break;
            case "header": block.Witness.Headers.Clear(); block.Witness.Headers.Add(ByteString.CopyFrom(new byte[] { 0xc0 })); break;
            case "linkage": block.Witness.Headers.Add(block.Witness.Headers[0]); break;
            case "trailing": block.Witness.Headers[0] = ByteString.CopyFrom([.. block.Witness.Headers[0].Span, 0x00]); break;
        }
        StreamingSession session = Session(block, mutation == "root" ? ByteString.CopyFrom(new byte[32]) : _parent.StateRoot);
        using SessionOperation operation = session.AdmitBlock(session.Binding, 1, block);
        AssertCode(() => _executor.Execute(operation, CancellationToken.None), StatusCode.FailedPrecondition);
        Assert.That(session.Ready(session.Binding, 2).Ready.ValidatedThrough, Is.EqualTo(block.Number - 1), "a rejected input cannot advance the session");
    }

    [Test]
    public void Execute_DuplicateWithMissingState_ReexecutesAndRejects()
    {
        StreamingSession session = Session(_block);
        using (SessionOperation operation = session.AdmitBlock(session.Binding, 1, _block))
            session.CommitBlock(operation, _executor.Execute(operation, CancellationToken.None));
        BlockWitness duplicate = _block.Clone();
        duplicate.Witness.State.Clear();
        using (SessionOperation operation = session.AdmitBlock(session.Binding, 2, duplicate))
            AssertCode(() => _executor.Execute(operation, CancellationToken.None), StatusCode.FailedPrecondition);
        using SessionOperation retry = session.AdmitBlock(session.Binding, 3, _block);
        Assert.That(session.CommitBlock(retry, _executor.Execute(retry, CancellationToken.None)).Validated.Hash, Is.EqualTo(_block.Hash),
            "a failed duplicate releases its worker and preserves valid retained progress");
    }

    [Test]
    public void Execute_CancelledBeforeExecution_KeepsAdmissionUntilDrain()
    {
        StreamingSession session = Session(_block);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        using SessionOperation operation = session.AdmitBlock(session.Binding, 1, _block);
        Assert.That(() => _executor.Execute(operation, cancellation.Token), Throws.TypeOf<OperationCanceledException>());
        AssertCode(() => session.AdmitBlock(session.Binding, 2, _block), StatusCode.ResourceExhausted);
        operation.Dispose();
        using SessionOperation retry = session.AdmitBlock(session.Binding, 3, _block);
        Assert.That(session.CommitBlock(retry, _executor.Execute(retry, CancellationToken.None)).Validated.Hash, Is.EqualTo(_block.Hash),
            "admission is available only after the cancelled operation drains");
    }

    [Test]
    public void Execute_CancelledDuringSpecLookup_RejectsAfterExecutionWithoutProgress()
    {
        StreamingSession session = Session(_block);
        using CancellationTokenSource cancellation = new();
        ISpecProvider spec = StatelessFixtures.ReadSpecProvider(Fixture, "chain-config.json");
        StreamingBlockExecutor executor = new(new CancellingSpecProvider(spec, cancellation), EezChainRules.Rollup0, LimboLogs.Instance);
        using SessionOperation operation = session.AdmitBlock(session.Binding, 1, _block);
        Assert.That(cancellation.IsCancellationRequested, Is.False, "cancellation must begin inside execution");
        Assert.That(() => executor.Execute(operation, cancellation.Token), Throws.TypeOf<OperationCanceledException>());
        Assert.That(cancellation.IsCancellationRequested, Is.True, "the real execution reached the spec lookup");
        Assert.That(session.Ready(session.Binding, 2).Ready.ValidatedThrough, Is.EqualTo(_parent.Number), "cancellation cannot acknowledge the executed block");
        AssertCode(() => session.AdmitBlock(session.Binding, 3, _block), StatusCode.ResourceExhausted);
    }

    [Test]
    public void Execute_RewindBeforeCommit_RejectsExecutedStaleResult()
    {
        StreamingSession session = Session(_block);
        using SessionOperation operation = session.AdmitBlock(session.Binding, 1, _block);
        ExecutedStreamingBlock executed = _executor.Execute(operation, CancellationToken.None);
        session.Rewind(session.Binding, 2, _parent.Hash);
        AssertCode(() => session.CommitBlock(operation, executed), StatusCode.Aborted);
        Assert.That(session.Ready(session.Binding, 3).Ready.ValidatedThrough, Is.EqualTo(_parent.Number), "a stale execution cannot reinsert rewound progress");
    }

    internal static void AssertRecordedBlock(EezStatelessBlock input, EezStatelessBlockResult expected, Hash256 expectedHash,
        ISpecProvider specProvider, EezChainRules rules)
    {
        BlockWitness block = new()
        {
            Number = expected.Block.Number,
            Hash = ByteString.CopyFrom(expectedHash.Bytes),
            ParentHash = ByteString.CopyFrom(expected.Block.ParentHash!.Bytes),
            Rlp = ByteString.CopyFrom(input.Rlp),
            Witness = new ExecutionWitness(),
        };
        block.Witness.State.Add(input.Witness.State.Select(static item => ByteString.CopyFrom(item)));
        block.Witness.Codes.Add(input.Witness.Codes.Select(static item => ByteString.CopyFrom(item)));
        block.Witness.Keys.Add(input.Witness.Keys.Select(static item => ByteString.CopyFrom(item)));
        block.Witness.Headers.Add(input.Witness.Headers.Select(static item => ByteString.CopyFrom(item)));
        StreamingSession session = new("composer", new Begin
        {
            RollupId = 1,
            AnchorNumber = expected.Parent.Number,
            AnchorHash = block.ParentHash,
            AnchorStateRoot = ByteString.CopyFrom(expected.PreStateRoot.Bytes),
        }, new StreamingSessionLimits(8, 16 * 1024 * 1024, 100_000));
        StreamingBlockExecutor executor = new(specProvider, rules, LimboLogs.Instance);
        using SessionOperation operation = session.AdmitBlock(session.Binding, 1, block);
        Assert.That(session.CommitBlock(operation, executor.Execute(operation, CancellationToken.None)).Validated.Hash,
            Is.EqualTo(ByteString.CopyFrom(expectedHash.Bytes)), "streamed execution matches the independent recorded block hash");
    }

    private StreamingSession Session(BlockWitness block, ByteString? root = null) => new("composer",
        new Begin { RollupId = 1, AnchorNumber = block.Number - 1, AnchorHash = block.ParentHash, AnchorStateRoot = root ?? _parent.StateRoot },
        new StreamingSessionLimits(8, 16 * 1024 * 1024, 100_000));

    private static ByteString Hash(string value) => ByteString.CopyFrom(new Hash256(value).Bytes);

    private static void AssertCode(Action action, StatusCode code) =>
        Assert.That(action, Throws.TypeOf<RpcException>().With.Property(nameof(RpcException.StatusCode)).EqualTo(code));

    private sealed class CancellingSpecProvider(ISpecProvider inner, CancellationTokenSource cancellation) : ISpecProvider
    {
        public ForkActivation? MergeBlockNumber => inner.MergeBlockNumber;
        public ulong TimestampFork => inner.TimestampFork;
        public UInt256? TerminalTotalDifficulty => inner.TerminalTotalDifficulty;
        public IReleaseSpec GenesisSpec => inner.GenesisSpec;
        public bool GenesisStateUnavailable => inner.GenesisStateUnavailable;
        public ulong? DaoBlockNumber => inner.DaoBlockNumber;
        public ulong? BeaconChainGenesisTimestamp => inner.BeaconChainGenesisTimestamp;
        public ulong NetworkId => inner.NetworkId;
        public ulong ChainId => inner.ChainId;
        public string SealEngine => inner.SealEngine;
        public ForkActivation[] TransitionActivations => inner.TransitionActivations;
        public void UpdateMergeTransitionInfo(ulong? blockNumber, UInt256? terminalTotalDifficulty = null) => inner.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);
        public IReleaseSpec GetSpec(ForkActivation activation)
        {
            cancellation.Cancel();
            return inner.GetSpec(activation);
        }
    }

    private readonly record struct BlockHeaderData(ulong Number, ByteString Hash, ByteString StateRoot);
}
