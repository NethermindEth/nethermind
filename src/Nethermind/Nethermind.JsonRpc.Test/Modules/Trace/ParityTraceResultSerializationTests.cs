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

using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.JsonRpc.Test.Data;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class ParityTraceResultSerializationTests : SerializationTestBase
    {
        [Test]
        public void Can_serialize()
        {
            ParityTraceResult result = new ParityTraceResult();
            result.GasUsed = 12345;
            result.Output = new byte[] {6, 7, 8, 9, 0};

            TestToJson(result, "{\"gasUsed\":\"0x3039\",\"output\":\"0x0607080900\"}");
        }
        
        [Test]
        public void Can_serialize_nulls()
        {
            ParityTraceResult result = new ParityTraceResult();

            TestToJson(result, "{\"gasUsed\":\"0x0\",\"output\":null}");
        }
    }
}
