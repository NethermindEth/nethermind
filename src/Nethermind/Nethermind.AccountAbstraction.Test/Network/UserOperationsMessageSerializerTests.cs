// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Network;
using Nethermind.Core;
using Nethermind.Network.Test.P2P;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.AccountAbstraction.Test.Network
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class UserOperationsMessageSerializerTests
    {
        [SetUp]
        public void Setup()
        {
            Rlp.RegisterDecoders(typeof(UserOperationDecoder).Assembly);
        }

        [Test]
        public void Roundtrip()
        {
            UserOperationsMessageSerializer serializer = new();

            UserOperation userOperation = new(new UserOperationRpc
            {
                Sender = new Address(1.ToString("x40")),
                Nonce = 1000,
                CallData = new byte[] { 1, 2 },
                InitCode = new byte[] { 3, 4 },
                CallGas = 5,
                VerificationGas = 6,
                PreVerificationGas = 7,
                MaxFeePerGas = 8,
                MaxPriorityFeePerGas = 1,
                Paymaster = new Address(2.ToString("x40")),
                PaymasterData = new byte[] { 5, 6 },
                Signature = new byte[] { 1, 2, 3 }
            });

            UserOperationsMessage message = new(new[] { new UserOperationWithEntryPoint(userOperation, new Address("0x90f3e1105e63c877bf9587de5388c23cdb702c6b")) });
            TestZero(serializer, message, "f856f8549400000000000000000000000000000000000000018203e88203048201020506070801940000000000000000000000000000000000000002820506830102039490f3e1105e63c877bf9587de5388c23cdb702c6b");

            // Meaning of RLP above:
            // f8
            // 41 = 65 (length of UserOperationMessage)
            // f8
            // 3f = 63 (length of UserOperation)
            // 94 = 148 (prefix of address)
            // 0000000000000000000000000000000000000001 (sender)
            // 82 = 130 = 128 + 2 (length of nonce)
            // 03e8 = 1000 (nonce)
            // 82 = 130 = 128 + 2 (length of InitCode)
            // 0304 (InitCode)
            // 82 = 130 = 128 + 2 (length of CallData)
            // 0102 (CallData)
            // 05 (CallGas)
            // 06 (VerificationGas)
            // 07 (PreVerificationGas
            // 08 (MaxFeePerGas)
            // 01 (MaxPriorityFeePerGas)
            // 94 = 148 (prefix of address)
            // 0000000000000000000000000000000000000002 (Paymaster address)
            // 82 = 130 = 128 + 2 (length of PaymasterData)
            // 0506 (PaymasterData)
            // 83 = 131 = 128 + 3 (length of Signature)
            // 010203 (Signature)

            message = new(new[] { new UserOperationWithEntryPoint(userOperation, new Address("0x90f3e1105e63c877bf9587de5388c23cdb702c6b")), new UserOperationWithEntryPoint(userOperation, new Address("0xdb8b5f6080a8e466b64a8d7458326cb650b3353f")) });
            TestZero(serializer, message, "f8acf8549400000000000000000000000000000000000000018203e88203048201020506070801940000000000000000000000000000000000000002820506830102039490f3e1105e63c877bf9587de5388c23cdb702c6bf8549400000000000000000000000000000000000000018203e882030482010205060708019400000000000000000000000000000000000000028205068301020394db8b5f6080a8e466b64a8d7458326cb650b3353f");

            // Meaning of RLP above:
            // f8
            // 82 = 130 (length of UserOperationMessage)
            // f8
            // 3f = 63 (length of first UserOperation)
            // all data similar to above - 9400000000000000000000000000000000000000018203e8820304820102050607080194000000000000000000000000000000000000000282050683010203
            // f8
            // 3f = 63 (length of second UserOperation)
            // all data again
        }

        [Test]
        public void Can_handle_empty()
        {
            UserOperationsMessageSerializer serializer = new();
            UserOperationsMessage message = new(new UserOperationWithEntryPoint[] { });

            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void To_string_empty()
        {
            UserOperationsMessage message = new(new UserOperationWithEntryPoint[] { });
            _ = message.ToString();
        }

        private static void TestZero(UserOperationsMessageSerializer serializer, UserOperationsMessage message, string expectedData)
        {
            IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024);
            IByteBuffer buffer2 = PooledByteBufferAllocator.Default.Buffer(1024);
            try
            {
                serializer.Serialize(buffer, message);
                UserOperationsMessage deserialized = serializer.Deserialize(buffer);
                // Abi is similar in deserialized and message, but for assertion there is some difference and an error. Line below excludes Abi from assertion
                deserialized.Should().BeEquivalentTo(message, options => options.Excluding(o => o.Path.EndsWith("Abi")));

                Assert.That(buffer.ReadableBytes, Is.EqualTo(0), "readable bytes");

                serializer.Serialize(buffer2, deserialized);

                buffer.SetReaderIndex(0);
                string allHex = buffer.ReadAllHex();
                Assert.That(buffer2.ReadAllHex(), Is.EqualTo(allHex), "test zero");

                allHex.Should().BeEquivalentTo(expectedData);
            }
            finally
            {
                buffer.Release();
                buffer2.Release();
            }
        }
    }
}
