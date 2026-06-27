// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.Rlp;
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
        public void Roundtrip_NoAccountsNoProofs_HasCorrectLength()
        {
            using AccountRangeMessage msg = new()
            {
                RequestId = 1,
                PathsWithAccounts = ArrayPoolList<PathWithAccount>.Empty(),
                Proofs = EmptyByteArrayList.Instance,
            };

            AccountRangeMessageSerializer serializer = new();
            Assert.That(serializer.Serialize(msg).ToHexString(), Is.EqualTo("c301c0c0"));
        }

        [Test]
        public void Roundtrip_NoAccountsNoProofs()
        {
            AccountRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                PathsWithAccounts = ArrayPoolList<PathWithAccount>.Empty(),
                Proofs = EmptyByteArrayList.Instance
            };

            AccountRangeMessageSerializer serializer = new();
            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_Many()
        {
            Account acc01 = Build.An.Account
                .WithBalance(1)
                .WithCode(Code0)
                .WithStorageRoot(new Hash256("0x10d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"))
                .TestObject;
            Account acc02 = Build.An.Account
                .WithBalance(2)
                .WithCode(Code1)
                .WithStorageRoot(new Hash256("0x20d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"))
                .TestObject;

            AccountRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                PathsWithAccounts = new ArrayPoolList<PathWithAccount>(2) { new(TestItem.KeccakA, acc01), new(TestItem.KeccakB, acc02) },
                Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(2) { TestItem.RandomDataA, TestItem.RandomDataB })
            };

            AccountRangeMessageSerializer serializer = new();
            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Roundtrip_EmptyStorageRoot()
        {
            Account acc01 = Build.An.Account
                .WithBalance(1)
                .WithCode(Code0)
                .WithStorageRoot(Keccak.EmptyTreeHash)
                .TestObject;

            AccountRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                PathsWithAccounts = new ArrayPoolList<PathWithAccount>(1) { new(TestItem.KeccakB, acc01) },
                Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(2) { TestItem.RandomDataA, TestItem.RandomDataB })
            };

            AccountRangeMessageSerializer serializer = new();
            SerializerTester.TestZero(serializer, msg);
        }

        [TestCase(SnapMessageLimits.MaxProofs, false)]
        [TestCase(SnapMessageLimits.MaxProofs + 1, true)]
        public void Deserialize_EnforcesProofsCountLimit(int proofCount, bool shouldThrow)
        {
            ArrayPoolList<byte[]> proofs = new(proofCount, Enumerable.Repeat(new byte[] { 0x42 }, proofCount));
            using AccountRangeMessage msg = new()
            {
                RequestId = 1,
                PathsWithAccounts = ArrayPoolList<PathWithAccount>.Empty(),
                Proofs = new ByteArrayListAdapter(proofs)
            };

            AccountRangeMessageSerializer serializer = new();
            byte[] serialized = serializer.Serialize(msg);

            if (shouldThrow)
            {
                Assert.Throws<RlpLimitException>(() => serializer.Deserialize(serialized));
            }
            else
            {
                using AccountRangeMessage deserialized = serializer.Deserialize(serialized);
                Assert.That(deserialized.Proofs.Count, Is.EqualTo(proofCount));
            }
        }

        [Test]
        public void Roundtrip_EmptyCode()
        {

            Account acc01 = Build.An.Account
                .WithBalance(1)
                .WithStorageRoot(TestItem.KeccakA)
                .TestObject;

            AccountRangeMessage msg = new()
            {
                RequestId = MessageConstants.Random.NextLong(),
                PathsWithAccounts = new ArrayPoolList<PathWithAccount>(1) { new(TestItem.KeccakB, acc01) },
                Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(2) { TestItem.RandomDataA, TestItem.RandomDataB })
            };

            AccountRangeMessageSerializer serializer = new();
            SerializerTester.TestZero(serializer, msg);
        }

        [Test]
        public void Deserialize_throws_on_null_account_path()
        {
            byte[] serialized = EncodeMessageWithNullAccountPath();
            AccountRangeMessageSerializer serializer = new();

            Assert.That(() => serializer.Deserialize(serialized), Throws.TypeOf<RlpException>());
        }

        [Test]
        public void Deserialize_throws_on_null_account()
        {
            byte[] serialized = EncodeMessageWithNullAccount();
            AccountRangeMessageSerializer serializer = new();

            Assert.That(() => serializer.Deserialize(serialized), Throws.TypeOf<RlpException>());
        }

        private static byte[] EncodeMessageWithNullAccountPath()
        {
            Account account = Build.An.Account
                .WithBalance(1)
                .WithStorageRoot(TestItem.KeccakA)
                .TestObject;
            AccountDecoder accountDecoder = new(true);

            int accountContentLength = accountDecoder.GetContentLength(account);
            int pathWithAccountContentLength = Rlp.LengthOf((Hash256?)null) + Rlp.LengthOfSequence(accountContentLength);
            int pathsWithAccountsContentLength = Rlp.LengthOfSequence(pathWithAccountContentLength);
            int contentLength = Rlp.LengthOf(1L)
                + Rlp.LengthOfSequence(pathsWithAccountsContentLength)
                + Rlp.LengthOfSequence(0);

            byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
            RlpWriter writer = new(bytes);
            writer.StartSequence(contentLength);
            writer.Encode(1L);
            writer.StartSequence(pathsWithAccountsContentLength);
            writer.StartSequence(pathWithAccountContentLength);
            writer.Encode((Hash256?)null);
            accountDecoder.Encode(account, ref writer, accountContentLength);
            writer.StartSequence(0);
            return bytes;
        }

        private static byte[] EncodeMessageWithNullAccount()
        {
            int pathWithAccountContentLength = Rlp.LengthOf(TestItem.KeccakA) + Rlp.LengthOfSequence(0);
            int pathsWithAccountsContentLength = Rlp.LengthOfSequence(pathWithAccountContentLength);
            int contentLength = Rlp.LengthOf(1L)
                + Rlp.LengthOfSequence(pathsWithAccountsContentLength)
                + Rlp.LengthOfSequence(0);

            byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
            RlpWriter writer = new(bytes);
            writer.StartSequence(contentLength);
            writer.Encode(1L);
            writer.StartSequence(pathsWithAccountsContentLength);
            writer.StartSequence(pathWithAccountContentLength);
            writer.Encode(TestItem.KeccakA);
            writer.StartSequence(0);
            writer.StartSequence(0);
            return bytes;
        }
    }
}
