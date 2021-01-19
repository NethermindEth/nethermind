//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.JsonRpc.Modules.Trace;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class ParityTxTraceFromReplayConverterTest : ParityLikeTxTraceSerializationTestBase
    {
        [Test]
        public void Trace_replay_transaction()
        {
            ParityLikeTxTrace[] trace = {BuildParityTxTrace(), BuildParityTxTrace()};
            TestToJson(trace.Select(t => new ParityTxTraceFromReplay(t)).ToArray(), "[{\"output\":null,\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":{\"*\":{\"from\":\"0x01\",\"to\":\"0x02\"}},\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}},\"trace\":[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"type\":null}],\"vmTrace\":null},{\"output\":null,\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":{\"*\":{\"from\":\"0x01\",\"to\":\"0x02\"}},\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}},\"trace\":[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"type\":null}],\"vmTrace\":null}]");
        }

        [TestCase(true, "{\"output\":null,\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":\"=\",\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}},\"trace\":[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"type\":null}],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"vmTrace\":null}")]
        [TestCase(false, "{\"output\":null,\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":\"=\",\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}},\"trace\":[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"type\":null}],\"vmTrace\":null}")]
        public void Can_serialize(bool includeTransactionHash, string expectedResult)
        {
            ParityTraceAction subtrace = new ParityTraceAction
            {
                Value = 67890,
                CallType = "call",
                From = TestItem.AddressC,
                To = TestItem.AddressD,
                Input = Array.Empty<byte>(),
                Gas = 10000,
                TraceAddress = new int[] {0, 0}
            };

            ParityLikeTxTrace result = new ParityLikeTxTrace
            {
                Action = new ParityTraceAction
                {
                    Value = 12345,
                    CallType = "init",
                    From = TestItem.AddressA,
                    To = TestItem.AddressB,
                    Input = new byte[] {1, 2, 3, 4, 5, 6},
                    Gas = 40000,
                    TraceAddress = new int[] {0}
                },
                BlockHash = TestItem.KeccakB,
                BlockNumber = 123456,
                TransactionHash = TestItem.KeccakC,
                TransactionPosition = 5
            };
            result.Action.TraceAddress = new int[] {1, 2, 3};
            result.Action.Subtraces.Add(subtrace);

            ParityAccountStateChange stateChange = new ParityAccountStateChange
            {
                Balance = new ParityStateChange<UInt256?>(1, 2),
                Nonce = new ParityStateChange<UInt256?>(0, 1),
                Storage = new Dictionary<UInt256, ParityStateChange<byte[]>> {[1] = new ParityStateChange<byte[]>(new byte[] {1}, new byte[] {2})}
            };

            result.StateChanges = new Dictionary<Address, ParityAccountStateChange> {{TestItem.AddressC, stateChange}};
            TestToJson(new ParityTxTraceFromReplay(result, includeTransactionHash), expectedResult);
        }

        [Test]
        [Todo(Improve.Refactor, "Different action serializers")]
        public void Can_serialize_reward()
        {
            Block block = Build.A.Block.WithNumber(long.Parse("4563918244f40000".AsSpan(), NumberStyles.AllowHexSpecifier)).TestObject;
            IBlockTracer blockTracer = new ParityLikeBlockTracer(ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            blockTracer.StartNewBlockTrace(block);
            ITxTracer txTracer = blockTracer.StartNewTxTrace(null);
            txTracer.ReportBalanceChange(TestItem.AddressA, 0, 3.Ether());
            blockTracer.EndTxTrace();
            blockTracer.ReportReward(TestItem.AddressA, "block", UInt256.One);

            ParityLikeTxTrace trace = ((ParityLikeBlockTracer) blockTracer).BuildResult().SingleOrDefault();

            TestToJson(new ParityTxTraceFromReplay(trace), "{\"output\":null,\"stateDiff\":{\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\":{\"balance\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x29a2241af62c0000\"}},\"code\":\"=\",\"nonce\":\"=\",\"storage\":{}}},\"trace\":[{\"action\":{\"author\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"rewardType\":\"block\",\"value\":\"0x1\"},\"result\":null,\"subtraces\":0,\"traceAddress\":[],\"type\":\"reward\"}],\"vmTrace\":null}");
        }
        
        [Test]
        public void Can_serialize_creation_method()
        {
            string expectedResult = "{\"output\":null,\"stateDiff\":{\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":\"=\",\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}},\"trace\":[{\"action\":{\"creationMethod\":\"create2\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"init\":\"0x010203040506\",\"value\":\"0x3039\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"type\":\"create\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"type\":null}],\"vmTrace\":null}";
            
            ParityTraceAction subtrace = new ParityTraceAction
            {
                Value = 67890,
                CallType = "call",
                From = TestItem.AddressC,
                To = TestItem.AddressD,
                Input = Array.Empty<byte>(),
                Gas = 10000,
                TraceAddress = new int[] {0, 0}
            };

            ParityLikeTxTrace result = new ParityLikeTxTrace
            {
                Action = new ParityTraceAction
                {
                    Value = 12345,
                    Type = "create",
                    CallType = "create",
                    CreationMethod = "create2",
                    From = TestItem.AddressA,
                    To = null,
                    Input = new byte[] {1, 2, 3, 4, 5, 6},
                    Gas = 40000,
                    TraceAddress = new int[] {0}
                },
                BlockHash = TestItem.KeccakB,
                BlockNumber = 123456,
                TransactionHash = TestItem.KeccakC,
                TransactionPosition = 5
            };
            result.Action.TraceAddress = new int[] {1, 2, 3};
            result.Action.Subtraces.Add(subtrace);

            ParityAccountStateChange stateChange = new ParityAccountStateChange
            {
                Balance = new ParityStateChange<UInt256?>(1, 2),
                Nonce = new ParityStateChange<UInt256?>(0, 1),
                Storage = new Dictionary<UInt256, ParityStateChange<byte[]>> {[1] = new ParityStateChange<byte[]>(new byte[] {1}, new byte[] {2})}
            };

            result.StateChanges = new Dictionary<Address, ParityAccountStateChange> {{TestItem.AddressC, stateChange}};
            TestToJson(new ParityTxTraceFromReplay(result, false), expectedResult);
        }

        [Test, Ignore("Reenable it after running compare on PoW chains")]
        public void Can_serialize_reward_state_only()
        {
            Block block = Build.A.Block.WithNumber(long.Parse("4563918244f40000".AsSpan(), NumberStyles.AllowHexSpecifier)).TestObject;
            IBlockTracer blockTracer = new ParityLikeBlockTracer(ParityTraceTypes.StateDiff);
            blockTracer.StartNewBlockTrace(block);
            ITxTracer txTracer = blockTracer.StartNewTxTrace(null);
            txTracer.ReportBalanceChange(TestItem.AddressA, 0, 3.Ether());
            blockTracer.EndTxTrace();
            blockTracer.ReportReward(TestItem.AddressA, "block", UInt256.One);

            ParityLikeTxTrace trace = ((ParityLikeBlockTracer) blockTracer).BuildResult().SingleOrDefault();

            TestToJson(trace, "{\"output\":null,\"stateDiff\":{\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\":{\"balance\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x29a2241af62c0000\"}},\"code\":\"=\",\"nonce\":\"=\",\"storage\":{}}},\"trace\":null,\"vmTrace\":null}");
        }
    }
}
