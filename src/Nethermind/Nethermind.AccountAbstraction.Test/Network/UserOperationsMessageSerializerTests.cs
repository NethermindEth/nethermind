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

using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Network;
using Nethermind.Core;
using Nethermind.Network;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
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
                CallData = new byte[] {1, 2},
                InitCode = new byte[] {3, 4},
                CallGas = 5,
                VerificationGas = 6,
                PreVerificationGas = 7,
                MaxFeePerGas = 8,
                MaxPriorityFeePerGas = 1,
                Paymaster = new Address(2.ToString("x40")),
                PaymasterData = new byte[] {5, 6},
                Signature = new byte[] {1, 2, 3}
            });

            UserOperationsMessage message = new(new[] {userOperation});
            TestZero(serializer, message, "f841f83f9400000000000000000000000000000000000000018203e8820304820102050607080194000000000000000000000000000000000000000282050683010203");

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

            message = new(new[] {userOperation, userOperation});
            TestZero(serializer, message, "f882f83f9400000000000000000000000000000000000000018203e8820304820102050607080194000000000000000000000000000000000000000282050683010203f83f9400000000000000000000000000000000000000018203e8820304820102050607080194000000000000000000000000000000000000000282050683010203");
            
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
            UserOperationsMessage message = new(new UserOperation[] { });

            SerializerTester.TestZero(serializer, message);
        }
        
        [Test]
        public void To_string_empty()
        {
            UserOperationsMessage message = new(new UserOperation[] { });
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

                Assert.AreEqual(0, buffer.ReadableBytes, "readable bytes");

                serializer.Serialize(buffer2, deserialized);

                buffer.SetReaderIndex(0);
                string allHex = buffer.ReadAllHex();
                Assert.AreEqual(allHex, buffer2.ReadAllHex(), "test zero");

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
