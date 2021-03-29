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
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class TxDecoderTests
    {
        private readonly TxDecoder _txDecoder = new TxDecoder();

        public static IEnumerable<(Transaction, string)> TestCaseSource()
        {
            yield return (Build.A.Transaction.SignedAndResolved().TestObject, "basic");
            yield return (Build.A.Transaction
                .WithData(new byte[] {1, 2, 3})
                .WithType(TxType.AccessList)
                .WithChainId(1)
                .WithAccessList(
                    new AccessList(
                        new Dictionary<Address, IReadOnlySet<UInt256>>
                        {
                            {Address.Zero, new HashSet<UInt256> {(UInt256)1}}
                        }, new Queue<object>(new List<object> {Address.Zero, (UInt256)1})))
                .SignedAndResolved().TestObject, "access list");
            yield return (Build.A.Transaction
                .WithData(new byte[] {1, 2, 3})
                .WithType(TxType.EIP1559)
                .WithFeeCap(30)
                .WithChainId(1)
                .WithAccessList(
                    new AccessList(
                        new Dictionary<Address, IReadOnlySet<UInt256>>
                        {
                            {Address.Zero, new HashSet<UInt256> {(UInt256)1}}
                        }, new Queue<object>(new List<object> {Address.Zero, (UInt256)1})))
                .SignedAndResolved().TestObject, "EIP1559 - access list");
            yield return (Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithFeeCap(50)
                .WithGasPremium(10)
                .WithChainId(0)
                .SignedAndResolved().TestObject, "EIP 1559");
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void Roundtrip((Transaction Tx, string Description) testCase)
        {
            RlpStream rlpStream = new RlpStream(10000);
            _txDecoder.Encode(rlpStream, testCase.Tx);
            rlpStream.Position = 0;
            Transaction decoded = _txDecoder.Decode(rlpStream);
            decoded!.SenderAddress = new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance).RecoverAddress(decoded);
            decoded.Hash = decoded.CalculateHash();
            decoded.Should().BeEquivalentTo(testCase.Tx, testCase.Description);
        }

        [TestCaseSource(nameof(YoloV3TestCases))]
        public void Roundtrip_yolo_v3((string IncomingRlpHex, Keccak Hash) testCase)
        {
            TestContext.Out.WriteLine($"Testing {testCase.Hash}");
            RlpStream incomingTxRlp = Bytes.FromHexString(testCase.IncomingRlpHex).AsRlpStream();
            Transaction decoded = _txDecoder.Decode(incomingTxRlp);
            decoded.CalculateHash().Should().Be(testCase.Hash);

            RlpStream ourRlpOutput = new RlpStream(incomingTxRlp.Length * 2);
            _txDecoder.Encode(ourRlpOutput, decoded);

            string ourRlpHex = ourRlpOutput.Data.AsSpan(0, incomingTxRlp.Length).ToHexString();
            ourRlpHex.Should().BeEquivalentTo(testCase.IncomingRlpHex);
        }
        
        [TestCaseSource(nameof(TestCaseSource))]
        public void Rlp_encode_should_return_the_same_as_rlp_stream_encoding((Transaction Tx, string Description) testCase)
        {
            Rlp rlpStreamResult = _txDecoder.Encode(testCase.Tx, RlpBehaviors.ForTreeRoot);
            Rlp rlpResult = Rlp.Encode(testCase.Tx, false, true, testCase.Tx.ChainId ?? 0);
            Transaction decodedRlpStream = _txDecoder.Decode(new RlpStream(rlpStreamResult.Bytes), RlpBehaviors.ForTreeRoot);
            Transaction decodedRlp = _txDecoder.Decode(new RlpStream(rlpResult.Bytes), RlpBehaviors.ForTreeRoot);
            Assert.AreEqual(decodedRlp?.Hash, decodedRlpStream?.Hash);
            Assert.AreEqual(rlpResult.Bytes, rlpStreamResult.Bytes);
        }

        public static IEnumerable<(string, Keccak)> YoloV3TestCases()
        {
            yield return
            (
                "b8a701f8a486796f6c6f763380843b9aca008262d4948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f838f7940000000000000000000000000000000000001337e1a0000000000000000000000000000000000000000000000000000000000000000080a0775101f92dcca278a56bfe4d613428624a1ebfc3cd9e0bcc1de80c41455b9021a06c9deac205afe7b124907d4ba54a9f46161498bd3990b90d175aac12c9a40ee9",
                new Keccak("0x8d6c458a40d2b06f56310eb1e1322138ee4fb2d78a1dc35e96ac166600529ce6")
            );
            yield return
            (
                "b8ca01f8c786796f6c6f763301843b9aca00826a40948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f85bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a01feaff3227c4fe4954fe5297898027d71eb9ae2291e2b967f00b2f5ccd0597baa053bfeb53c31024700b8d3b226eb60766178b17f215c3a5b5bd7fa2c45db86fb8",
                new Keccak("0x7b967be8cc1c3a899f25a7285dd8be24212b25502f2f31828fe10c8db0d24073")
            );
            yield return
            (
                "b8ca01f8c786796f6c6f763302843b9aca00826a40948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f85bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000001a05dd874c3cbf30fa22f2612c4dda995e53dda6f3aad335760bccd0fe3ae65dadda056208b02dac8246ecbf4624c8b49302e4869781e630ebba356e13d532166ba5d",
                new Keccak("0x80f26aceb2e3b0812329adb03c666a8bb3cd9b5aa0355712e6b44be3a6f975d3")
            );

            yield return
            (
                "b8ca01f8c786796f6c6f763303843b9aca00826a40948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f85bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a072515bdc69de9eb8e5067ffcd069ec84745ea629cfc60b854edccdd6a9d1d80fa063bb9c012fdb80aabdb915bea8e3c99b574e88cf37daea4dcc535627b48b56f0",
                new Keccak("0x9efbb6d928c6ad37efef58c98ebfd2bc3dd22f75955a6f171a4dbcf27f47d067")
            );
            yield return
            (
                "b9018201f9017e86796f6c6f763304843b9aca00829ab0948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f90111f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a09e41e382c76d2913521d7191ecced4a1a16fe0f3e5e22d83d50dd58adbe409e1a07c0e036eff80f9ca192ac26d533fc49c280d90c8b62e90c1a1457b50e51e6144",
                new Keccak("0xe81972d75cfec02375e7ddf3a38f2da80719d1d2b87ae973f6dc06bffc26db7e")
            );
            yield return
            (
                "f86905843b9aca00825208948a8eafb1cf62bfbeb1741769dae1a9dd47996192018086f2ded8deec8aa04135bba08382dae6a1d5ec4b557f2460e1d63fb6f93773a7a951ce38a28a31ada03d36a791688f311252df622a48a9acfb0500fd3584a8305ee004d895c0257400",
                new Keccak("0x593dd0e1bf113b762674470741817c4d823c73fb7377da4f6073c7885585ae92")
            );
        }
    }
}
