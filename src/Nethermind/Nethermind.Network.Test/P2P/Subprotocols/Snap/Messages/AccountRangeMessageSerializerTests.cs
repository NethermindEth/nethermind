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
// 

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

            AccountRangeMessage msg = new() { 
                RequestId = MessageConstants.Random.NextLong(),
                PathsWithAccounts = new[] { new PathWithAccount(TestItem.KeccakB, acc01) },
                Proofs = new[] {TestItem.RandomDataA, TestItem.RandomDataB}
            };

            AccountRangeMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, msg);
        }
    }
}
