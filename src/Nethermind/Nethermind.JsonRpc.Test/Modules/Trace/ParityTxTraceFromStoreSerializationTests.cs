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

using System.Linq;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.JsonRpc.Modules.Trace;
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
            ParityLikeTxTrace[] trace = {BuildParityTxTrace(), BuildParityTxTrace()};
            TestToJson(trace.SelectMany(ParityTxTraceFromStore.FromTxTrace).ToArray(), "[{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"blockHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockNumber\":123456,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"transactionPosition\":5,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"blockHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockNumber\":123456,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"transactionPosition\":5,\"type\":null},{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"blockHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockNumber\":123456,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"transactionPosition\":5,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0x2710\",\"input\":\"0x\",\"to\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"value\":\"0x10932\"},\"blockHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockNumber\":123456,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"traceAddress\":[0,0],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"transactionPosition\":5,\"type\":null}]");
        }

        [Test]
        public void Can_serialize()
        {
            var trace = ParityTxTraceFromStore.FromTxTrace(BuildParityTxTrace()).First();
            TestToJson(trace, "{\"action\":{\"callType\":\"init\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c40\",\"input\":\"0x010203040506\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x3039\"},\"blockHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockNumber\":123456,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":1,\"traceAddress\":[1,2,3],\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"transactionPosition\":5,\"type\":null}");
        }
    }
}
