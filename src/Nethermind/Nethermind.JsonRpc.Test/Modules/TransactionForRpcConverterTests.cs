// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

            Transaction tx = new();
            tx.Signature = new Signature(r, s, 27);

            TransactionForRpc txForRpc = new(tx);

            EthereumJsonSerializer serializer = new();
            string serialized = serializer.Serialize(txForRpc);

            serialized.Should().Contain("0x20000000000000000000000000000000000000000000000000000000000");
            serialized.Should().Contain("0x1000000000000000000000000000000000000000000000000000000000000");
            Console.WriteLine(serialized);
        }
    }
}
