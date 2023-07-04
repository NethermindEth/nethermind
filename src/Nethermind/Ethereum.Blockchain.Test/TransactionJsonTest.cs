// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

            Transaction tx = JsonToEthereumTest.Convert(new PostStateJson { Indexes = new IndexesJson() }, txJson);
            tx.AccessList.Should().NotBeNull();
        }
    }
}
