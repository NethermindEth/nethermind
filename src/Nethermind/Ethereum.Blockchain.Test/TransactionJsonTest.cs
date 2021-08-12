/*
 * Copyright (c) 2021 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Ethereum.Test.Base;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class TransactionJsonTest : GeneralStateTestBase
    {
        [Test]
        public void Can_load_access_lists()
        {
            const string lists =
                "{\"accessLists\": [[{address: \"0x0001020304050607080900010203040506070809\", storageKeys: [\"0x00\", \"0x01\"]}]]}";

            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            TransactionJson txJson = serializer.Deserialize<TransactionJson>(lists);
            txJson.SecretKey = TestItem.PrivateKeyA.KeyBytes;
            txJson.Value = new UInt256[1];
            txJson.GasLimit = new long[1];
            txJson.Data = new byte[1][];
            txJson.AccessLists.Should().NotBeNull();
            txJson.AccessLists[0][0].Address.Should()
                .BeEquivalentTo(new Address("0x0001020304050607080900010203040506070809"));
            txJson.AccessLists[0][0].StorageKeys[1][0].Should().Be((byte)1);

            Transaction tx = JsonToEthereumTest.Convert(new PostStateJson {Indexes = new IndexesJson()}, txJson);
            tx.AccessList.Should().NotBeNull();
        }
    }
}
