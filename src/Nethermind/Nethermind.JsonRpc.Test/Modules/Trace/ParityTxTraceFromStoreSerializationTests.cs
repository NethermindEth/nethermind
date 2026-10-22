// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace
{
    [TestFixture]
    [Parallelizable(ParallelScope.Self)]
    public class ParityTxTraceFromStoreSerializationTests : ParityLikeTxTraceSerializationTestBase
    {
        [Test]
        public void Trace_replay_transaction()
        {
            ParityLikeTxTrace[] trace = { BuildParityTxTrace(), BuildParityTxTrace() };
            TestToJson(trace.SelectMany(ParityTxTraceFromStore.FromTxTrace).ToArray(), "[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"blockHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockNumber\":123456,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"transactionPosition\":5},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"blockHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockNumber\":123456,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"transactionPosition\":5},{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"blockHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockNumber\":123456,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"transactionPosition\":5},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"blockHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockNumber\":123456,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"transactionPosition\":5}]");
        }

        [Test]
        public void Can_serialize()
        {
            ParityTxTraceFromStore trace = ParityTxTraceFromStore.FromTxTrace(BuildParityTxTrace()).First();
            TestToJson(trace, "{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"blockHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockNumber\":123456,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"transactionPosition\":5}");
        }

        [Test]
        public void FromTxTrace_flattens_depth_first_and_skips_excluded_subtrees()
        {
            string[] traceAddresses = ParityTxTraceFromStore.FromTxTrace(BuildNestedTrace())
                .Select(static t => string.Join(",", t.TraceAddress.AsSpan().ToArray()))
                .ToArray();

            Assert.That(traceAddresses, Is.EqualTo(new[] { "", "0", "0,0", "0,0,0", "0,1", "1", "2", "4" }));
        }

        // A halted frame, a suicide and a reward have no result, and a reward belongs to no transaction.
        [TestCase("0,1", false)]
        [TestCase("4", false)]
        [TestCase("", true)]
        public void Writes_an_absent_result_or_transaction_as_null(string traceAddress, bool reward)
        {
            ParityTxTraceFromStore record = ParityTxTraceFromStore.FromTxTrace(reward ? BuildRewardTrace() : BuildNestedTrace())
                .Single(t => string.Join(",", t.TraceAddress.AsSpan().ToArray()) == traceAddress);

            using JsonDocument document = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(record));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Kind("result"), Is.EqualTo(JsonValueKind.Null));
                Assert.That(Kind("transactionHash"), Is.EqualTo(reward ? JsonValueKind.Null : JsonValueKind.String));
                Assert.That(Kind("transactionPosition"), Is.EqualTo(reward ? JsonValueKind.Null : JsonValueKind.Number));
            }

            JsonValueKind Kind(string member) =>
                document.RootElement.TryGetProperty(member, out JsonElement value) ? value.ValueKind : JsonValueKind.Undefined;
        }

        [TestCaseSource(nameof(WriteCases))]
        public void Write_matches_serialized_items(ParityLikeTxTrace trace, Address[]? toAddresses, ulong after, ulong? count)
        {
            TxTraceFilter? writeFilter = toAddresses is null && count is null ? null : new(null, toAddresses, after, count, TraceFilterMode.Intersection);
            TxTraceFilter? itemsFilter = writeFilter is null ? null : new(null, toAddresses, after, count, TraceFilterMode.Intersection);
            IEnumerable<ParityTxTraceFromStore> items = ParityTxTraceFromStore.FromTxTrace(trace);
            ParityTxTraceFromStore[] expectedItems = (itemsFilter?.FilterTxTraces(items) ?? items).ToArray();

            string actual = WriteJson(writer =>
            {
                writer.WriteStartArray();
                ParityTxTraceFromStore.Write(writer, trace, writeFilter, EthereumJsonSerializer.JsonOptions);
                writer.WriteEndArray();
            });
            string expected = WriteJson(writer => TypeInfoJsonSerializer.Serialize(writer, expectedItems, EthereumJsonSerializer.JsonOptions));

            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void Write_stops_at_the_next_action_once_cancelled()
        {
            using CancellationTokenSource cts = new();
            CancellingBufferWriter buffer = new(cts);
            using Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { SkipValidation = true });

            Assert.Throws<OperationCanceledException>(() => ParityTxTraceFromStore.Write(writer, BuildNestedTrace(), null, EthereumJsonSerializer.JsonOptions, cts.Token));
        }

        /// <summary>Cancels the token as soon as the first action starts writing.</summary>
        private sealed class CancellingBufferWriter(CancellationTokenSource cts) : IBufferWriter<byte>
        {
            private readonly ArrayBufferWriter<byte> _inner = new();

            public void Advance(int count) => _inner.Advance(count);

            public Memory<byte> GetMemory(int sizeHint = 0)
            {
                cts.Cancel();
                return _inner.GetMemory(sizeHint);
            }

            public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        }

        private static IEnumerable<TestCaseData> WriteCases()
        {
            yield return new TestCaseData(BuildParityTxTrace(), null, 0ul, null).SetName("Write_simple");
            yield return new TestCaseData(BuildNestedTrace(), null, 0ul, null).SetName("Write_nested");
            yield return new TestCaseData(BuildNestedTrace(), new[] { TestItem.AddressB }, 1ul, 2ul).SetName("Write_nested_filtered");
            yield return new TestCaseData(BuildNestedTrace(), new[] { TestItem.AddressF }, 0ul, null).SetName("Write_nested_filtered_none");
            yield return new TestCaseData(BuildRewardTrace(), null, 0ul, null).SetName("Write_reward");
            yield return new TestCaseData(new ParityLikeTxTrace(), null, 0ul, null).SetName("Write_no_action");
            yield return new TestCaseData(new ParityLikeTxTrace { Action = new ParityTraceAction { IncludeInTrace = false } }, null, 0ul, null).SetName("Write_excluded_root");
        }

        private static string WriteJson(System.Action<Utf8JsonWriter> write)
        {
            ArrayBufferWriter<byte> buffer = new();
            using (Utf8JsonWriter writer = new(buffer))
            {
                write(writer);
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        private static ParityTraceAction Action(string type, Address to, int[] traceAddress, params ParityTraceAction[] subtraces)
        {
            ParityTraceAction action = new()
            {
                Type = type,
                CallType = type == "call" ? "call" : null,
                From = TestItem.AddressA,
                To = to,
                Gas = 21000,
                Value = 1,
                Input = new byte[] { 0xab },
                TraceAddress = traceAddress,
                Result = new ParityTraceResult { GasUsed = 100, Output = [1, 2] }
            };
            action.Subtraces.AddRange(subtraces);
            return action;
        }

        private static ParityLikeTxTrace BuildNestedTrace()
        {
            ParityTraceAction create = Action("create", TestItem.AddressC, [1]);
            create.CreationMethod = "create2";
            create.Result = new ParityTraceResult { GasUsed = 5, Address = TestItem.AddressC, Code = [0x60] };

            ParityTraceAction reverted = Action("call", TestItem.AddressB, [2]);
            reverted.Error = "Reverted";

            ParityTraceAction excluded = Action("call", TestItem.AddressB, [3], Action("call", TestItem.AddressB, [3, 0]));
            excluded.IncludeInTrace = false;

            ParityTraceAction selfDestruct = Action("suicide", TestItem.AddressD, [4]);
            selfDestruct.Result = null;

            ParityTraceAction failed = Action("call", TestItem.AddressB, [0, 1]);
            failed.Error = "Out of gas";
            failed.Result = null;

            ParityTraceAction root = Action("call", TestItem.AddressB, [],
                Action("call", TestItem.AddressB, [0], Action("call", TestItem.AddressB, [0, 0], Action("call", TestItem.AddressB, [0, 0, 0])), failed),
                create,
                reverted,
                excluded,
                selfDestruct);

            return new ParityLikeTxTrace
            {
                Action = root,
                BlockHash = TestItem.KeccakA,
                BlockNumber = 7,
                TransactionHash = TestItem.KeccakB,
                TransactionPosition = 0
            };
        }

        private static ParityLikeTxTrace BuildRewardTrace() => new()
        {
            BlockHash = TestItem.KeccakA,
            BlockNumber = 7,
            Action = new ParityTraceAction
            {
                RewardType = "block",
                Value = 2,
                Author = TestItem.AddressE,
                CallType = "reward",
                TraceAddress = default,
                Type = "reward",
                Result = null
            }
        };
    }
}
