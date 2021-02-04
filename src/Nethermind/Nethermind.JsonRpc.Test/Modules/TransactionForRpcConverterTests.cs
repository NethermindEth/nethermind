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
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Test.Data;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class TransactionForRpcConverterTests : SerializationTestBase
    {
        [Test]
        public void R_and_s_are_quantity_and_not_data()
        {
            byte[] r = new byte[32];
            byte[] s = new byte[32];
            r[1] = 1;
            s[2] = 2;
            
            Transaction tx = new Transaction();
            tx.Signature = new Signature(r, s, 27);
            
            TransactionForRpc txForRpc = new TransactionForRpc(tx);

            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            string serialized = serializer.Serialize(txForRpc);

            serialized.Should().Contain("0x20000000000000000000000000000000000000000000000000000000000");
            serialized.Should().Contain("0x1000000000000000000000000000000000000000000000000000000000000");
            Console.WriteLine(serialized);
        }
    }
}
