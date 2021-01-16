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

using System.Collections.Generic;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.JsonRpc.Test.Data;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class ParityAccountStateChangeSerializationTests : SerializationTestBase
    {
        [Test]
        public void Can_serialize()
        {
            ParityAccountStateChange result = new ParityAccountStateChange();
            result.Balance = new ParityStateChange<UInt256?>(1, 2);
            result.Nonce = new ParityStateChange<UInt256?>(0, 1);
            result.Storage = new Dictionary<UInt256, ParityStateChange<byte[]>>();
            result.Storage[1] = new ParityStateChange<byte[]>(new byte[] {1}, new byte[] {2});

            TestToJson(result, "{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":\"=\",\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}");
        }

        [Test]
        public void Can_serialize_null_to_1_change()
        {
            ParityAccountStateChange result = new ParityAccountStateChange();
            result.Balance = new ParityStateChange<UInt256?>(null, 1);

            TestToJson(result, "{\"balance\":{\"+\":\"0x1\"},\"code\":\"=\",\"nonce\":\"=\",\"storage\":{}}");
        }
        
        [Test]
        public void Can_serialize_1_to_null()
        {
            ParityAccountStateChange result = new ParityAccountStateChange();
            result.Balance = new ParityStateChange<UInt256?>(1, null);

            TestToJson(result, "{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":null}},\"code\":\"=\",\"nonce\":\"=\",\"storage\":{}}");
        }
        
        [Test]
        public void Can_serialize_nulls()
        {
            ParityAccountStateChange result = new ParityAccountStateChange();

            TestToJson(result, "{\"balance\":\"=\",\"code\":\"=\",\"nonce\":\"=\",\"storage\":{}}");
        }
    }
}
