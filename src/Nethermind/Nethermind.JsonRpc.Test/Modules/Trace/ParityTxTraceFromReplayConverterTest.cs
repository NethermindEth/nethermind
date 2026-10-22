// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class ParityTxTraceFromReplayConverterTest : ParityLikeTxTraceSerializationTestBase
    {
        [TestCase("0x00", "0x0")]
        [TestCase("0x01", "0x1")]
        [TestCase("0x0f", "0xf")]
        [TestCase("0x10", "0x10")]
        [TestCase("0x2a", "0x2a")]
        [TestCase("0x0100", "0x100")]
        [TestCase("0x8000000000000000000000000000000000000000000000000000000000000000", "0x8000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        public void Vm_stack_and_storage_words_are_quantities_while_code_and_memory_are_data(string input, string expected)
        {
            byte[] word = Bytes.FromHexString(input).PadLeft(32);
            ParityVmOperationTrace operation = new()
            {
                Push = [word],
                Memory = new ParityMemoryChangeTrace { Data = [0, 1], Offset = 0 },
                Store = new ParityStorageChangeTrace { Key = word, Value = Bytes.FromHexString(input) },
                Sub = new ParityVmTrace
                {
                    Code = [0, 1],
                    Operations = [new ParityVmOperationTrace { Push = [word] }]
                }
            };
            ParityLikeTxTrace trace = new()
            {
                VmTrace = new ParityVmTrace { Code = [0, 1], Operations = [operation] }
            };

            string json = new EthereumJsonSerializer().Serialize(new ParityTxTraceFromReplay(trace));
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement vm = document.RootElement.GetProperty("vmTrace");
            JsonElement op = vm.GetProperty("ops")[0];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(op.GetProperty("ex").GetProperty("push")[0].GetString(), Is.EqualTo(expected));
                Assert.That(op.GetProperty("ex").GetProperty("store").GetProperty("key").GetString(), Is.EqualTo(expected));
                Assert.That(op.GetProperty("ex").GetProperty("store").GetProperty("val").GetString(), Is.EqualTo(expected));
                Assert.That(op.GetProperty("sub").GetProperty("ops")[0].GetProperty("ex").GetProperty("push")[0].GetString(), Is.EqualTo(expected));
                Assert.That(vm.GetProperty("code").GetString(), Is.EqualTo("0x0001"));
                Assert.That(op.GetProperty("sub").GetProperty("code").GetString(), Is.EqualTo("0x0001"));
                Assert.That(op.GetProperty("ex").GetProperty("mem").GetProperty("data").GetString(), Is.EqualTo("0x0001"));
            }
        }

        [Test]
        public void Vm_push_preserves_null_and_empty_arrays([Values] bool missing)
        {
            ParityVmOperationTrace operation = new();
            if (!missing) operation.Push = [];
            string json = new EthereumJsonSerializer().Serialize(operation);
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.GetProperty("ex").GetProperty("push").GetRawText(), Is.EqualTo(missing ? "null" : "[]"));
        }

        [Test]
        public void Trace_replay_transaction()
        {
            ParityLikeTxTrace[] trace = { BuildParityTxTrace(), BuildParityTxTrace() };
            TestToJson(trace.Select(static t => new ParityTxTraceFromReplay(t)).ToArray(), "[{\"output\":null,\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":{\"*\":{\"from\":\"0x01\",\"to\":\"0x02\"}},\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}},\"trace\":[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"type\":null}],\"vmTrace\":null},{\"output\":null,\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":{\"*\":{\"from\":\"0x01\",\"to\":\"0x02\"}},\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}},\"trace\":[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"type\":null}],\"vmTrace\":null}]");
        }

        [TestCase(true, "{\"output\":null,\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":\"=\",\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}},\"trace\":[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[0,0],\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0,0],\"type\":null}],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"vmTrace\":null}")]
        [TestCase(false, "{\"output\":null,\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":\"=\",\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}},\"trace\":[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[0,0],\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0,0],\"type\":null}],\"vmTrace\":null}")]
        public void Can_serialize(bool includeTransactionHash, string expectedResult)
        {
            ParityTraceAction innerSubtrace = new()
            {
                Value = 67890,
                CallType = "call",
                From = TestItem.AddressC,
                To = TestItem.AddressD,
                Input = CappedArray<byte>.Empty,
                Gas = 10000,
                TraceAddress = new int[] { 0, 0, 0 }
            };

            ParityTraceAction subtrace = new()
            {
                Value = 67890,
                CallType = "call",
                From = TestItem.AddressC,
                To = TestItem.AddressD,
                Input = CappedArray<byte>.Empty,
                Gas = 10000,
                TraceAddress = new int[] { 0, 0 }
            };

            subtrace.Subtraces.Add(innerSubtrace);

            ParityLikeTxTrace result = new()
            {
                Action = new ParityTraceAction
                {
                    Value = 12345,
                    CallType = "init",
                    From = TestItem.AddressA,
                    To = TestItem.AddressB,
                    Input = new byte[] { 1, 2, 3, 4, 5, 6 },
                    Gas = 40000,
                    TraceAddress = new int[] { 0 }
                },
                BlockHash = TestItem.KeccakB,
                BlockNumber = 123456,
                TransactionHash = TestItem.KeccakC,
                TransactionPosition = 5
            };
            result.Action.TraceAddress = new int[] { 1, 2, 3 };
            result.Action.Subtraces.Add(subtrace);

            ParityAccountStateChange stateChange = new()
            {
                Balance = new ParityStateChange<UInt256?>(1, 2),
                Nonce = new ParityStateChange<UInt256?>(0, 1),
                Storage = new Dictionary<UInt256, ParityStateChange<byte[]>> { [1] = new(new byte[] { 1 }, new byte[] { 2 }) }
            };

            result.StateChanges = new Dictionary<Address, ParityAccountStateChange> { { TestItem.AddressC, stateChange } };
            TestToJson(new ParityTxTraceFromReplay(result, includeTransactionHash), expectedResult);
        }

        [Test]
        public void Serialize_WhenStateDiffAccountsAreUnordered_BothWritersListThemByAscendingAddress()
        {
            Address[] addresses = [TestItem.AddressF, TestItem.AddressA, TestItem.AddressD, TestItem.AddressC, TestItem.AddressE, TestItem.AddressB];
            ParityLikeTxTrace trace = new() { StateChanges = [] };
            foreach (Address address in addresses)
            {
                trace.StateChanges[address] = new ParityAccountStateChange { Balance = new ParityStateChange<UInt256?>(1, 2) };
            }

            string[] ascending = [.. addresses.Select(static a => a.ToString()).Order(StringComparer.Ordinal)];

            string replayJson = new EthereumJsonSerializer().Serialize(new ParityTxTraceFromReplay(trace));
            ArrayBufferWriter<byte> buffer = new();
            using (Utf8JsonWriter writer = new(buffer))
            {
                ParityReplayEnvelopeWriter.WriteFromTrace(writer, trace, includeTxHash: false, EthereumJsonSerializer.JsonOptions);
            }

            using JsonDocument replay = JsonDocument.Parse(replayJson);
            using JsonDocument streamed = JsonDocument.Parse(buffer.WrittenMemory);
            JsonElement replayDiff = replay.RootElement.GetProperty("stateDiff");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(replayDiff.EnumerateObject().Select(static p => p.Name), Is.EqualTo(ascending), "the buffered replay writes stateDiff accounts in ascending address order");
                Assert.That(streamed.RootElement.GetProperty("stateDiff").GetRawText(), Is.EqualTo(replayDiff.GetRawText()), "the streamed envelope writes the same stateDiff as the buffered replay");
            }
        }

        [Test]
        [Todo(Improve.Refactor, "Different action serializers")]
        public void Can_serialize_reward()
        {
            Block block = Build.A.Block.WithNumber(ulong.Parse("4563918244f40000".AsSpan(), NumberStyles.AllowHexSpecifier)).TestObject;
            IBlockTracer blockTracer = new ParityLikeBlockTracer(ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            blockTracer.StartNewBlockTrace(block);
            ITxTracer txTracer = blockTracer.StartNewTxTrace(null);
            txTracer.ReportBalanceChange(TestItem.AddressA, 0, 3.Ether);
            blockTracer.EndTxTrace();
            blockTracer.ReportReward(TestItem.AddressA, "block", UInt256.One);

            ParityLikeTxTrace trace = ((ParityLikeBlockTracer)blockTracer).BuildResult().SingleOrDefault()!;

            TestToJson(new ParityTxTraceFromReplay(trace), "{\"output\":null,\"stateDiff\":{\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\":{\"balance\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x29a2241af62c0000\"}},\"code\":\"=\",\"nonce\":\"=\",\"storage\":{}}},\"trace\":[{\"action\":{\"author\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"rewardType\":\"block\",\"value\":\"0x1\"},\"result\":null,\"subtraces\":0,\"traceAddress\":[],\"type\":\"reward\"}],\"vmTrace\":null}");
        }

        [Test]
        public void Can_serialize_creation_method()
        {
            string expectedResult = "{\"output\":null,\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":\"=\",\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}},\"trace\":[{\"action\":{\"creationMethod\":\"create2\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"init\":\"0x010203040506\",\"value\":\"0x3039\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"type\":\"create\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"type\":null}],\"vmTrace\":null}";

            ParityTraceAction subtrace = new()
            {
                Value = 67890,
                CallType = "call",
                From = TestItem.AddressC,
                To = TestItem.AddressD,
                Input = CappedArray<byte>.Empty,
                Gas = 10000,
                TraceAddress = new int[] { 0, 0 }
            };

            ParityLikeTxTrace result = new()
            {
                Action = new ParityTraceAction
                {
                    Value = 12345,
                    Type = "create",
                    CallType = "create",
                    CreationMethod = "create2",
                    From = TestItem.AddressA,
                    To = null,
                    Input = new byte[] { 1, 2, 3, 4, 5, 6 },
                    Gas = 40000,
                    TraceAddress = new int[] { 0 }
                },
                BlockHash = TestItem.KeccakB,
                BlockNumber = 123456,
                TransactionHash = TestItem.KeccakC,
                TransactionPosition = 5
            };
            result.Action.TraceAddress = new int[] { 1, 2, 3 };
            result.Action.Subtraces.Add(subtrace);

            ParityAccountStateChange stateChange = new()
            {
                Balance = new ParityStateChange<UInt256?>(1, 2),
                Nonce = new ParityStateChange<UInt256?>(0, 1),
                Storage = new Dictionary<UInt256, ParityStateChange<byte[]>> { [1] = new(new byte[] { 1 }, new byte[] { 2 }) }
            };

            result.StateChanges = new Dictionary<Address, ParityAccountStateChange> { { TestItem.AddressC, stateChange } };
            TestToJson(new ParityTxTraceFromReplay(result, false), expectedResult);
        }

        [Test, Ignore("Reenable it after running compare on PoW chains")]
        public void Can_serialize_reward_state_only()
        {
            Block block = Build.A.Block.WithNumber(ulong.Parse("4563918244f40000".AsSpan(), NumberStyles.AllowHexSpecifier)).TestObject;
            IBlockTracer blockTracer = new ParityLikeBlockTracer(ParityTraceTypes.StateDiff);
            blockTracer.StartNewBlockTrace(block);
            ITxTracer txTracer = blockTracer.StartNewTxTrace(null);
            txTracer.ReportBalanceChange(TestItem.AddressA, 0, 3.Ether);
            blockTracer.EndTxTrace();
            blockTracer.ReportReward(TestItem.AddressA, "block", UInt256.One);

            ParityLikeTxTrace trace = ((ParityLikeBlockTracer)blockTracer).BuildResult().SingleOrDefault()!;

            TestToJson(trace, "{\"output\":null,\"stateDiff\":{\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\":{\"balance\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x29a2241af62c0000\"}},\"code\":\"=\",\"nonce\":\"=\",\"storage\":{}}},\"trace\":null,\"vmTrace\":null}");
        }
    }
}
