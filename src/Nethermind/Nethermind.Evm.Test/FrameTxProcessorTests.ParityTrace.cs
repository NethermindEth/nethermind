// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
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
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public partial class FrameTxProcessorTests
{
    private const ParityTraceTypes AllParityTraceTypes = ParityTraceTypes.Trace | ParityTraceTypes.StateDiff | ParityTraceTypes.VmTrace;
    private static readonly Address ParitySponsor = TestItem.AddressD;
    private static readonly Address ParityReverter = TestItem.AddressF;

    /// <summary>Frame transactions covering nested calls, a skipped frame, a frame that never enters the VM, and
    /// a sponsored payer. <see cref="Observer"/> calls <see cref="Recipient"/>, so its frame has a subtrace.</summary>
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
                DeployContract(ParityReverter, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);
                frames =
                [
                    SelfVerifyFrame(),
                    Frame(FrameMode.Sender, flags: FrameFlags.AtomicBatch, target: ParityReverter),
                    Frame(FrameMode.Sender, target: Recipient),
                    Frame(FrameMode.Default, target: Observer)
                ];
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

        tx.Hash = Keccak.Compute(scenario);
        return tx;
    }

    [TestCase("nested", new[] { 0, 1, 2 }, new[] { 1, 0 })]
    [TestCase("skipped", new[] { 0, 1, 3 }, new[] { 3, 0 })]
    [TestCase("undispatched", new[] { 0, 2 }, new[] { 2, 0 })]
    [TestCase("sponsored", new[] { 0, 1, 2 }, new[] { 2, 0 })]
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
            Assert.That(root.Gas, Is.EqualTo(tx.GasLimit));
            Assert.That(root.Result?.GasUsed, Is.EqualTo(receipt.GasUsed));
            Assert.That(root.Subtraces, Has.Count.EqualTo(frames.Length));

            for (int i = 0; i < root.Subtraces.Count; i++)
            {
                ParityTraceAction frame = root.Subtraces[i];
                bool isStatic = frames[i].Mode is FrameMode.Verify or FrameMode.PostTx;
                Assert.That(frame.TraceAddress.ToArray(), Is.EqualTo(new[] { i }), $"frame {i} address");
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
                        break;
                }
            }

            Assert.That(flat.Count(t => t.TraceAddress.ToArray().SequenceEqual(nestedCall) && t.Action.To == Recipient), Is.EqualTo(1), "nested call");
            Assert.That(trace.VmTrace!.Code ?? [], Is.Empty, "root code");
            Assert.That(trace.VmTrace.Operations.Select(static o => o.Pc), Is.EqualTo(framesInVm), "one root operation per frame that entered the VM");
            Assert.That(trace.VmTrace.Operations.All(static o => o.Sub?.Operations.Count > 0), Is.True, "every root operation carries its frame");
            // Every frame's code opens with a PUSH, which a gas carry-over from the previous frame would misprice.
            Assert.That(trace.VmTrace.Operations.Select(static o => o.Sub!.Operations[0].Cost), Is.All.EqualTo(GasCostOf.VeryLow), "first operation of each frame");
        }
    }

    [Test]
    public void ParityTrace_FrameTx_StreamedMatchesBuffered(
        [Values("nested", "skipped", "undispatched", "sponsored")] string scenario, [Values] ParityTraceStreamMode mode)
    {
        string buffered = BufferedParityJson(scenario, mode);
        TearDown();
        Setup();
        string streamed = StreamedParityJson(scenario, mode);

        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(streamed), JsonNode.Parse(buffered)), Is.True,
            $"streamed:{streamed}{System.Environment.NewLine}buffered:{buffered}");
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

    private (ParityLikeTxTrace Trace, TxReceipt Receipt) TraceParity(Transaction tx, ParityTraceTypes types)
    {
        ParityLikeBlockTracer blockTracer = new(types);
        TxReceipt receipt = RunThroughReceiptsTracer(tx, blockTracer);
        return (blockTracer.BuildResult().Single(), receipt);
    }

    private string BufferedParityJson(string scenario, ParityTraceStreamMode mode)
    {
        (ParityLikeTxTrace trace, _) = TraceParity(ParityScenarioTx(scenario), AllParityTraceTypes);
        return mode == ParityTraceStreamMode.Replay
            ? JsonSerializer.Serialize(new[] { new ParityTxTraceFromReplay(trace, includeTransactionHash: true) }, EthereumJsonSerializer.JsonOptions)
            : JsonSerializer.Serialize(ParityTxTraceFromStore.FromTxTrace(trace), EthereumJsonSerializer.JsonOptions);
    }

    private string StreamedParityJson(string scenario, ParityTraceStreamMode mode)
    {
        Transaction tx = ParityScenarioTx(scenario);
        ArrayBufferWriter<byte> sink = new();
        using (Utf8JsonWriter writer = new(sink))
        {
            writer.WriteStartArray();
            using StreamingParityLikeBlockTracer blockTracer = new(AllParityTraceTypes, mode, includeTxHash: true, writer, pipeWriter: null, CancellationToken.None);
            RunThroughReceiptsTracer(tx, blockTracer);
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(sink.WrittenSpan);
    }

    /// <summary>Runs <paramref name="tx"/> through the chain the tracing RPCs build: the receipts tracer over the
    /// cancellable block tracer.</summary>
    private TxReceipt RunThroughReceiptsTracer(Transaction tx, IBlockTracer blockTracer)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithBeneficiary(Beneficiary)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.SetOtherTracer(blockTracer.WithCancellation(CancellationToken.None));
        receiptsTracer.StartNewBlockTrace(block);
        receiptsTracer.StartNewTxTrace(tx);
        Assert.That(_transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), receiptsTracer).TransactionExecuted, Is.True);
        receiptsTracer.EndTxTrace();
        receiptsTracer.EndBlockTrace();
        return receiptsTracer.TxReceipts[0];
    }
}
