// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public partial class FrameTxProcessorTests
{
    private const ParityTraceTypes AllParityTraceTypes = ParityTraceTypes.Trace | ParityTraceTypes.StateDiff | ParityTraceTypes.VmTrace;
    private static readonly Address ParitySponsor = TestItem.AddressD;
    private static readonly Address ParityReverter = TestItem.AddressF;
    private static readonly Address ParityTailCaller = TestItem.Addresses[10];
    private const string RevertOutput = "0x00";

    /// <summary>Reverts with one byte of untouched memory, so its output is not empty.</summary>
    private static readonly byte[] RevertingWithOutput = Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.REVERT).Done;

    /// <summary>Frame transactions covering nested calls, a skipped frame, a frame that never enters the VM, a
    /// sponsored payer, a frame ending on a CALL, a reverted POST_TX frame, a static frame calling a
    /// precompile, and keyed nonces whose calldata the processor measures only once it runs. <see cref="Observer"/> calls <see cref="Recipient"/>, so its frame has a subtrace.</summary>
    private Transaction ParityScenarioTx(string scenario)
    {
        DeployContract(Recipient, LogEmitter(7));
        TxFrame[] frames;
        switch (scenario)
        {
            case "sponsored":
                DeployContract(Sender, ApproveCode(FrameFlags.ApproveExecution), 1.Ether);
                DeployContract(ParitySponsor, ApproveCode(FrameFlags.ApprovePayment), 1.Ether);
                frames =
                [
                    new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecution, target: null, gasLimit: 200_000, UInt256.Zero, default),
                    new TxFrame(FrameMode.Verify, FrameFlags.ApprovePayment, ParitySponsor, gasLimit: 200_000, UInt256.Zero, default),
                    Frame(FrameMode.Sender, target: Observer)
                ];
                break;
            case "skipped":
                DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
                DeployContract(ParityReverter, RevertingWithOutput);
                frames =
                [
                    SelfVerifyFrame(),
                    Frame(FrameMode.Sender, flags: FrameFlags.AtomicBatch, target: ParityReverter),
                    Frame(FrameMode.Sender, target: Recipient),
                    Frame(FrameMode.Default, target: Observer)
                ];
                break;
            case "tailCall":
                DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
                // No trailing STOP: the frame ends on the CALL's return, with no operation after it.
                DeployContract(ParityTailCaller, Prepare.EvmCode.Call(Recipient, 50_000).Done);
                frames = [SelfVerifyFrame(), Frame(FrameMode.Sender, target: ParityTailCaller), Frame(FrameMode.Default, target: Recipient)];
                break;
            case "postTxReverted":
                DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
                DeployContract(ParityReverter, RevertingWithOutput);
                frames =
                [
                    SelfVerifyFrame(),
                    Frame(FrameMode.Sender, target: Observer),
                    Frame(FrameMode.PostTx, target: ParityReverter, stateGasLimit: 0),
                    Frame(FrameMode.PostTx, target: Observer, stateGasLimit: 0)
                ];
                break;
            case "precompileFrame":
                DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
                frames =
                [
                    SelfVerifyFrame(),
                    Frame(FrameMode.Sender, target: Observer),
                    Frame(FrameMode.PostTx, target: IdentityPrecompile.Address, stateGasLimit: 0)
                ];
                break;
            case "keyedNonces":
                DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
                frames = [KeyedSelfVerifyFrame(freshKeyCount: 2), Frame(FrameMode.Sender, target: Observer)];
                break;
            case "undispatched":
                DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
                frames = [SelfVerifyFrame(), UndispatchedFrame(PreDispatchExit.EntryAccessCharge, out _), Frame(FrameMode.Default, target: Observer)];
                break;
            default:
                DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
                frames = [SelfVerifyFrame(), Frame(FrameMode.Sender, target: Observer), Frame(FrameMode.Default, target: Recipient)];
                break;
        }

        // After the frames, as an undispatched frame deploys its own code at Observer.
        DeployContract(Observer, Prepare.EvmCode.Call(Recipient, 50_000).Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0, frames);
        // As the decoder sets it: a frame transaction has no gas_limit field of its own.
        foreach (TxFrame frame in frames)
        {
            tx.GasLimit += frame.GasLimit;
        }

        if (scenario == "keyedNonces")
        {
            tx.NonceKeys = [1, 7];
        }

        tx.Hash = Keccak.Compute(scenario);
        return tx;
    }

    [TestCase("nested", new[] { 0, 1, 2 }, new[] { 1, 0 })]
    [TestCase("skipped", new[] { 0, 1, 3 }, new[] { 3, 0 })]
    [TestCase("undispatched", new[] { 0, 2 }, new[] { 2, 0 })]
    [TestCase("sponsored", new[] { 0, 1, 2 }, new[] { 2, 0 })]
    [TestCase("tailCall", new[] { 0, 1, 2 }, new[] { 1, 0 })]
    [TestCase("postTxReverted", new[] { 0, 1, 2 }, new[] { 1, 0 })]
    [TestCase("precompileFrame", new[] { 0, 1, 2 }, new[] { 1, 0 })]
    [TestCase("keyedNonces", new[] { 0, 1 }, new[] { 1, 0 })]
    public void ParityTrace_FrameTx_RootsEveryFrameAtItsIndex(string scenario, int[] framesInVm, int[] nestedCall)
    {
        Transaction tx = ParityScenarioTx(scenario);
        (ParityLikeTxTrace trace, TxReceipt receipt) = TraceParity(tx, ParityTraceTypes.Trace | ParityTraceTypes.VmTrace);
        TxFrame[] frames = tx.Frames!;
        TxFrameReceipt[] frameReceipts = receipt.FrameReceipts!;
        ParityTraceAction root = trace.Action!;
        ParityTxTraceFromStore[] flat = ParityTxTraceFromStore.FromTxTrace(trace).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.TraceAddress.Length, Is.Zero);
            Assert.That(root.CallType, Is.EqualTo("call"));
            Assert.That(root.From, Is.EqualTo(Sender));
            Assert.That(root.To, Is.EqualTo(Eip8141Constants.EntryPointAddress));
            Assert.That(root.Gas, Is.EqualTo(FrameGasBudget(tx)), "root gas");
            Assert.That(root.Result?.GasUsed ?? 0, Is.LessThanOrEqualTo(root.Gas), "root gasUsed");
            Assert.That(root.Result?.GasUsed, Is.EqualTo(receipt.GasUsed), "root gasUsed");
            // The receipt's status is derived from the frames, and the root's error follows it.
            Assert.That(root.Error, Is.EqualTo(scenario == "postTxReverted" ? "POST_TX frame reverted"
                : receipt.StatusCode == StatusCode.Success ? null : "frame failed"), "root error");

            Assert.That(root.Subtraces, Has.Count.EqualTo(frames.Length));
            Assert.That(frameReceipts.Count(static r => r.Status == TxFrameReceipt.StatusSkipped), Is.EqualTo(scenario is "skipped" or "postTxReverted" ? 1 : 0), "skipped frames");

            for (int i = 0; i < root.Subtraces.Count; i++)
            {
                ParityTraceAction frame = root.Subtraces[i];
                bool isStatic = frames[i].Mode is FrameMode.Verify or FrameMode.PostTx;
                Assert.That(frame.TraceAddress, Is.SequenceEqualTo(new[] { i }), $"frame {i} address");
                Assert.That(frame.CallType, Is.EqualTo(isStatic ? "staticcall" : "call"), $"frame {i} call type");
                Assert.That(frame.From, Is.EqualTo(frames[i].Mode == FrameMode.Sender ? Sender : Eip8141Constants.EntryPointAddress), $"frame {i} from");
                Assert.That(frame.To, Is.EqualTo(frames[i].Target ?? Sender), $"frame {i} to");
                Assert.That(frame.Gas, Is.EqualTo(frames[i].GasLimit), $"frame {i} gas");
                switch (frameReceipts[i].Status)
                {
                    case TxFrameReceipt.StatusSuccess:
                        Assert.That(frame.Error, Is.Null, $"frame {i} error");
                        Assert.That(frame.Result?.GasUsed, Is.EqualTo(frameReceipts[i].GasUsed), $"frame {i} gasUsed");
                        break;
                    case TxFrameReceipt.StatusSkipped:
                        Assert.That(frame.Error, Is.EqualTo("frame skipped"), $"frame {i} error");
                        break;
                    default:
                        Assert.That(frame.Error, Is.Not.Null, $"frame {i} error");
                        if (frame.Error == "Reverted")
                        {
                            Assert.That(frame.Result?.GasUsed, Is.EqualTo(frameReceipts[i].GasUsed), $"frame {i} reverted gasUsed");
                            Assert.That(frame.Result?.Output?.ToHexString(true), Is.EqualTo(RevertOutput), $"frame {i} revert output");
                        }
                        else
                        {
                            Assert.That(frame.Result, Is.Null, $"frame {i} result");
                        }

                        break;
                }
            }

            Assert.That(flat.Count(static t => t.TraceAddress.Length == 1), Is.EqualTo(frames.Length), "every frame is rendered");
            Assert.That(RevertedFrameOutputs(trace), Is.EqualTo(root.Subtraces.Where(static f => f.Error == "Reverted").Select(static _ => RevertOutput)),
                "a reverted frame's replay entry keeps its result");
            Assert.That(flat.Count(t => t.TraceAddress.ToArray().SequenceEqual(nestedCall) && t.Action.To == Recipient), Is.EqualTo(1), "nested call");
            Assert.That(trace.VmTrace!.Code ?? [], Is.Empty, "root code");
            Assert.That(trace.VmTrace.Operations.Select(static o => o.Pc), Is.EqualTo(framesInVm), "one root operation per frame that entered the VM");
            // Per the execution-apis frame transaction trace profile: the frame's gas limit, less its receipt's gasUsed.
            Assert.That(trace.VmTrace.Operations.Select(o => o.Cost), Is.EqualTo(framesInVm.Select(i => frames[i].GasLimit)), "root operation cost");
            Assert.That(trace.VmTrace.Operations.Select(static o => o.Used),
                Is.EqualTo(framesInVm.Select(i => frames[i].GasLimit - frameReceipts[i].GasUsed)), "root operation ex.used");
            Assert.That(trace.VmTrace.Operations.All(static o => o.Sub is not null), Is.True, "every root operation carries its frame");
            // Every frame's code opens with a PUSH, which a gas carry-over from the previous frame would misprice.
            Assert.That(trace.VmTrace.Operations.Where(static o => o.Sub.Operations.Count > 0).Select(static o => o.Sub.Operations[0].Cost),
                Is.All.EqualTo(GasCostOf.VeryLow), "first operation of each frame");
        }
    }

    [Test]
    public void ParityTrace_FrameTx_StreamedMatchesBuffered(
        [Values("nested", "skipped", "undispatched", "sponsored", "tailCall", "postTxReverted", "precompileFrame", "keyedNonces")] string scenario, [Values] ParityTraceStreamMode mode)
    {
        string buffered = BufferedParityJson(mode, ParityScenarioTx(scenario));
        TearDown();
        Setup();
        string streamed = StreamedParityJson(mode, ParityScenarioTx(scenario));

        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(streamed), JsonNode.Parse(buffered)), Is.True,
            $"streamed:{streamed}{System.Environment.NewLine}buffered:{buffered}");
    }

    /// <summary>A frame ending on its nested call's return, driven without the resume gas update that normally
    /// clears the call's gas carry-over, must not pass it to the next frame's first operation.</summary>
    [Test]
    public void ParityVmTrace_FrameEndingOnACallReturn_DoesNotCarryTheCallGasIntoTheNextFrame()
    {
        Transaction tx = FrameTx(nonce: 0, Frame(FrameMode.Sender, target: Observer), Frame(FrameMode.Default, target: Recipient));
        ParityLikeTxTracer tracer = new(Build.A.Block.WithTransactions(tx).TestObject, tx, ParityTraceTypes.Trace | ParityTraceTypes.VmTrace);
        IFrameTxReceiptTracer frameTracer = tracer;
        tracer.ReportAction(100_000, UInt256.Zero, Sender, Observer, default, ExecutionType.TRANSACTION);
        tracer.StartOperation(0, Instruction.CALL, 100_000, null!);
        tracer.ReportOperationRemainingGas(40_000);
        tracer.ReportAction(50_000, UInt256.Zero, Observer, Recipient, default, ExecutionType.CALL);
        tracer.StartOperation(0, Instruction.STOP, 50_000, null!);
        tracer.ReportOperationRemainingGas(50_000);
        tracer.ReportActionEnd(50_000, default);
        tracer.ReportActionEnd(90_000, default);
        frameTracer.ReportFrameEnd(0, null);

        tracer.ReportAction(100_000, UInt256.Zero, Eip8141Constants.EntryPointAddress, Recipient, default, ExecutionType.TRANSACTION);
        tracer.StartOperation(0, Instruction.PUSH1, 100_000, null!);
        tracer.ReportStackPush([1]);
        tracer.ReportOperationRemainingGas(100_000 - GasCostOf.VeryLow);
        tracer.StartOperation(2, Instruction.STOP, 100_000 - GasCostOf.VeryLow, null!);
        tracer.ReportOperationRemainingGas(100_000 - GasCostOf.VeryLow);
        tracer.ReportActionEnd(100_000 - GasCostOf.VeryLow, default);
        frameTracer.ReportFrameEnd(1, null);

        frameTracer.ReportFrameTxReceipt(Sender,
        [
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 10_000, 0, []),
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, GasCostOf.VeryLow, 0, [])
        ]);
        tracer.MarkAsSuccess(Eip8141Constants.EntryPointAddress, default, [], []);

        Assert.That(tracer.BuildResult().VmTrace!.Operations[1].Sub.Operations[0].Cost, Is.EqualTo(GasCostOf.VeryLow), "the second frame's PUSH1");
    }

    [TestCase("nested")]
    [TestCase("sponsored")]
    public void ParityTrace_FrameTx_StateDiffHasTheFeeDebitAndCredit(string scenario)
    {
        Transaction tx = ParityScenarioTx(scenario);
        (ParityLikeTxTrace trace, TxReceipt receipt) = TraceParity(tx, ParityTraceTypes.StateDiff);
        // Base fee 0 and 1 wei fees: the payer pays, and the beneficiary earns, one wei per gas.
        UInt256 fee = receipt.GasUsed;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.StateChanges!.TryGetValue(receipt.Payer!, out ParityAccountStateChange? payer), Is.True, "payer");
            Assert.That(payer?.Balance?.Before - payer?.Balance?.After, Is.EqualTo(fee), "payer debit");
            Assert.That(trace.StateChanges.TryGetValue(Beneficiary, out ParityAccountStateChange? beneficiary), Is.True, "beneficiary");
            Assert.That((beneficiary?.Balance?.After ?? 0) - (beneficiary?.Balance?.Before ?? 0), Is.EqualTo(fee), "beneficiary credit");
            Assert.That(trace.StateChanges[Sender].Nonce?.After, Is.EqualTo((UInt256?)1), "sender nonce");
        }
    }

    [TestCase("nested")]
    [TestCase("undispatched")]
    [TestCase("postTxReverted")]
    [TestCase("keyedNonces")]
    public void CallTrace_FrameTx_RootGasIsTheTransactionBudget(string scenario)
    {
        Transaction tx = ParityScenarioTx(scenario);
        // As debug_traceCall builds it: the processor measures the calldata the budget counts only once it runs.
        Assert.That(tx.FrameCalldataStats, Is.EqualTo(default((int, int))), "unmeasured when the tracer is built");
        (JsonDocument trace, _) = TraceCall(tx);
        using (trace)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(HexValue(trace.RootElement, "gas"), Is.EqualTo(FrameGasBudget(tx)), "root gas");
                Assert.That(HexValue(trace.RootElement, "gasUsed"), Is.LessThanOrEqualTo(HexValue(trace.RootElement, "gas")), "root gasUsed");
            }
        }
    }

    /// <summary>The gas the processor reserves for <paramref name="tx"/>: its frame limits plus the intrinsic gas.</summary>
    private ulong FrameGasBudget(Transaction tx)
    {
        Assert.That(FrameTxValidation.TryCalculateGasBudget(tx, Spec, out _, out _, out ulong maxGas), Is.True);
        Assert.That(maxGas, Is.GreaterThan(tx.GasLimit), "the budget exceeds the frame limits");
        return maxGas;
    }

    /// <summary>The <c>result.output</c> of each <c>trace_replay*</c> entry with error <c>Reverted</c>, or <c>null</c> without one.</summary>
    private static string?[] RevertedFrameOutputs(ParityLikeTxTrace trace)
    {
        using JsonDocument replay = JsonDocument.Parse(JsonSerializer.Serialize(new ParityTxTraceFromReplay(trace), EthereumJsonSerializer.JsonOptions));
        return replay.RootElement.GetProperty("trace").EnumerateArray()
            .Where(static t => t.TryGetProperty("error", out JsonElement error) && error.GetString() == "Reverted")
            .Select(static t => t.TryGetProperty("result", out JsonElement result) ? result.GetProperty("output").GetString() : null)
            .ToArray();
    }

    /// <summary>One block tracer across two blocks, as <c>trace_filter</c> uses it, prices each frame root with its
    /// own block's spec.</summary>
    [Test]
    public void ParityTrace_FrameTx_RootGasFollowsEachBlocksSpec([Values] bool streaming)
    {
        Transaction first = ParityScenarioTx("keyedNonces");
        Transaction second = ParityScenarioTx("keyedNonces");
        second.NonceKeys = [2, 8];
        second.Hash = Keccak.Compute("keyedNonces-second");
        // Without EIP-8250 the nonce-key calldata the budget otherwise counts is not priced.
        IReleaseSpec secondSpec = new OverridableReleaseSpec(Spec) { IsEip8250Enabled = false };
        CustomSpecProvider tracerSpecs = new(((ForkActivation)0, Spec), ((ForkActivation)2, secondSpec));

        ulong[] rootGas = streaming ? StreamedRootGas(tracerSpecs, first, second) : BufferedRootGas(tracerSpecs, first, second);

        Assert.That(FrameTxValidation.TryCalculateGasBudget(second, secondSpec, out _, out _, out ulong secondBudget), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondBudget, Is.Not.EqualTo(FrameGasBudget(second)), "the two specs price the second transaction differently");
            Assert.That(rootGas, Is.EqualTo(new[] { FrameGasBudget(first), secondBudget }));
        }
    }

    private ulong[] BufferedRootGas(ISpecProvider specProvider, params Transaction[] txs)
    {
        ParityLikeBlockTracer blockTracer = new(ParityTraceTypes.Trace, specProvider);
        ulong[] rootGas = new ulong[txs.Length];
        for (int i = 0; i < txs.Length; i++)
        {
            RunThroughReceiptsTracer(txs[i], blockTracer, blockNumber: (ulong)i + 1);
            rootGas[i] = blockTracer.BuildResult().Single().Action!.Gas;
        }

        return rootGas;
    }

    private ulong[] StreamedRootGas(ISpecProvider specProvider, params Transaction[] txs)
    {
        ArrayBufferWriter<byte> sink = new();
        using (Utf8JsonWriter writer = new(sink))
        {
            writer.WriteStartArray();
            using StreamingParityLikeBlockTracer blockTracer = new(ParityTraceTypes.Trace, ParityTraceStreamMode.Store, includeTxHash: false,
                writer, pipeWriter: null, CancellationToken.None, specProvider: specProvider);
            for (int i = 0; i < txs.Length; i++)
            {
                RunThroughReceiptsTracer(txs[i], blockTracer, blockNumber: (ulong)i + 1);
            }

            writer.WriteEndArray();
        }

        using JsonDocument document = JsonDocument.Parse(sink.WrittenMemory);
        return document.RootElement.EnumerateArray()
            .Where(static t => t.GetProperty("traceAddress").GetArrayLength() == 0)
            .Select(static t => HexValue(t.GetProperty("action"), "gas"))
            .ToArray();
    }

    /// <summary>One reused streaming tracer switches between streaming a legacy transaction's vmTrace and buffering a
    /// frame transaction's, and back.</summary>
    [Test]
    public void ParityTrace_BlockMixingLegacyAndFrameTxs_StreamedMatchesBuffered([Values] ParityTraceStreamMode mode)
    {
        string buffered = BufferedParityJson(mode, MixedBlockTxs());
        TearDown();
        Setup();
        string streamed = StreamedParityJson(mode, MixedBlockTxs());

        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(streamed), JsonNode.Parse(buffered)), Is.True,
            $"streamed:{streamed}{System.Environment.NewLine}buffered:{buffered}");
    }

    /// <summary>A legacy call, a frame transaction, then another legacy call, each running code.</summary>
    private Transaction[] MixedBlockTxs()
    {
        Transaction frameTx = ParityScenarioTx("nested");
        PrivateKey legacySender = TestItem.PrivateKeys[20];
        DeployContract(legacySender.Address, [], 1.Ether);
        Transaction LegacyCall(ulong nonce) => Build.A.Transaction.WithNonce(nonce).WithTo(Observer)
            .WithGasLimit(200_000).WithGasPrice(1).SignedAndResolved(legacySender).TestObject;
        return [LegacyCall(0), frameTx, LegacyCall(1)];
    }

    /// <summary>A VERIFY frame that reverts invalidates the transaction before any receipt is reported, so its
    /// operation keeps the frame's gas limit and the gas the VM left.</summary>
    [Test]
    public void ParityVmTrace_FrameTxWithoutReceipts_KeepsTheFrameLimitAndTheGasTheVmLeft()
    {
        DeploySmartSender(RevertingWithOutput);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        Block block = Build.A.Block.WithNumber(1).WithBaseFeePerGas(0).WithBeneficiary(Beneficiary)
            .WithTransactions(tx).WithGasLimit(30_000_000).TestObject;
        ParityLikeTxTracer tracer = new(block, tx, ParityTraceTypes.Trace | ParityTraceTypes.VmTrace, Spec);

        TransactionResult result = _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), tracer);
        ParityLikeTxTrace trace = tracer.BuildResult();
        ParityTraceAction frame = trace.Action!.Subtraces.Single();
        ParityVmOperationTrace operation = trace.VmTrace!.Operations.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False, "a reverted VERIFY frame invalidates the transaction");
            Assert.That(frame.Error, Is.EqualTo("Reverted"));
            Assert.That(operation.Cost, Is.EqualTo(tx.Frames![0].GasLimit), "cost");
            Assert.That(operation.Used, Is.EqualTo(frame.Gas - frame.Result!.GasUsed).And.GreaterThan(0UL), "ex.used");
        }
    }

    private (ParityLikeTxTrace Trace, TxReceipt Receipt) TraceParity(Transaction tx, ParityTraceTypes types)
    {
        ParityLikeBlockTracer blockTracer = new(types, _specProvider);
        TxReceipt receipt = RunThroughReceiptsTracer(tx, blockTracer);
        return (blockTracer.BuildResult().Single(), receipt);
    }

    private string BufferedParityJson(ParityTraceStreamMode mode, params Transaction[] txs)
    {
        ParityLikeBlockTracer blockTracer = new(AllParityTraceTypes, _specProvider);
        RunBlockThroughReceiptsTracer(blockTracer, blockNumber: 1, txs);
        IReadOnlyCollection<ParityLikeTxTrace> traces = blockTracer.BuildResult();
        return mode == ParityTraceStreamMode.Replay
            ? JsonSerializer.Serialize(traces.Select(static t => new ParityTxTraceFromReplay(t, includeTransactionHash: true)), EthereumJsonSerializer.JsonOptions)
            : JsonSerializer.Serialize(ParityTxTraceFromStore.FromTxTrace(traces), EthereumJsonSerializer.JsonOptions);
    }

    private string StreamedParityJson(ParityTraceStreamMode mode, params Transaction[] txs)
    {
        ArrayBufferWriter<byte> sink = new();
        using (Utf8JsonWriter writer = new(sink))
        {
            writer.WriteStartArray();
            using StreamingParityLikeBlockTracer blockTracer = new(AllParityTraceTypes, mode, includeTxHash: true, writer, pipeWriter: null, CancellationToken.None, specProvider: _specProvider);
            RunBlockThroughReceiptsTracer(blockTracer, blockNumber: 1, txs);
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(sink.WrittenSpan);
    }

    /// <summary>Runs <paramref name="tx"/> through the chain the tracing RPCs build: the receipts tracer over the
    /// cancellable block tracer.</summary>
    private TxReceipt RunThroughReceiptsTracer(Transaction tx, IBlockTracer blockTracer, ulong blockNumber = 1) =>
        RunBlockThroughReceiptsTracer(blockTracer, blockNumber, tx)[0];

    private TxReceipt[] RunBlockThroughReceiptsTracer(IBlockTracer blockTracer, ulong blockNumber, params Transaction[] txs)
    {
        Block block = Build.A.Block.WithNumber(blockNumber)
            .WithBaseFeePerGas(0)
            .WithBeneficiary(Beneficiary)
            .WithTransactions(txs)
            .WithGasLimit(30_000_000).TestObject;
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.SetOtherTracer(blockTracer.WithCancellation(CancellationToken.None));
        receiptsTracer.StartNewBlockTrace(block);
        foreach (Transaction tx in txs)
        {
            receiptsTracer.StartNewTxTrace(tx);
            Assert.That(_transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), receiptsTracer).TransactionExecuted, Is.True);
            receiptsTracer.EndTxTrace();
        }

        receiptsTracer.EndBlockTrace();
        return receiptsTracer.TxReceipts.ToArray();
    }
}
