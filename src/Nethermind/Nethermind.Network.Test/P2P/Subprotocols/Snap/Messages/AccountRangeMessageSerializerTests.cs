// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.State.Snap;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class AccountRangeMessageSerializerTests
    {
        public static readonly byte[] Code0 = { 0, 0 };
        public static readonly byte[] Code1 = { 0, 1 };

        [Test]
        public void Roundtrip_NoAccountsNoProofs()
        {
            AccountRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                PathsWithAccounts = System.Array.Empty<PathWithAccount>(),
                Proofs = Array.Empty<byte[]>()
            };

            AccountRangeMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_Many()
        {
            var acc01 = Build.An.Account
                .WithBalance(1)
                .WithCode(Code0)
                .WithStorageRoot(new Keccak("0x10d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"))
                .TestObject;
            var acc02 = Build.An.Account
                .WithBalance(2)
                .WithCode(Code1)
                .WithStorageRoot(new Keccak("0x20d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"))
                .TestObject;

            AccountRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                PathsWithAccounts = new[] { new PathWithAccount(TestItem.KeccakA, acc01), new PathWithAccount(TestItem.KeccakB, acc02) },
                Proofs = new[] { TestItem.RandomDataA, TestItem.RandomDataB }
            };

            AccountRangeMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_EmptyStorageRoot()
        {
            var acc01 = Build.An.Account
                .WithBalance(1)
                .WithCode(Code0)
                .WithStorageRoot(Keccak.EmptyTreeHash)
                .TestObject;

            AccountRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                PathsWithAccounts = new[] { new PathWithAccount(TestItem.KeccakB, acc01) },
                Proofs = new[] { TestItem.RandomDataA, TestItem.RandomDataB }
            };

            AccountRangeMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_EmptyCode()
        {

            var acc01 = Build.An.Account
                .WithBalance(1)
                .WithStorageRoot(TestItem.KeccakA)
                .TestObject;

            AccountRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                PathsWithAccounts = new[] { new PathWithAccount(TestItem.KeccakB, acc01) },
                Proofs = new[] { TestItem.RandomDataA, TestItem.RandomDataB }
            };

            AccountRangeMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }
    }
}
