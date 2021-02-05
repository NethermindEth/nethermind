//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections.Generic;
using FluentAssertions;
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
                .WithData(new byte[]{1,2,3})
                .WithType(TxType.AccessList)
                .WithAccessList(
                    new AccessList(
                        new Dictionary<Address, IReadOnlySet<UInt256>>
                        {
                            {Address.Zero, new HashSet<UInt256> {(UInt256)1}}
                        }))
                .SignedAndResolved().TestObject, "accessL list");
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void Roundtrip((Transaction Tx, string Description) testCase)
        {
            RlpStream rlpStream = new RlpStream(10000);
            _txDecoder.Encode(rlpStream, testCase.Tx, RlpBehaviors.UseTransactionTypes);
            rlpStream.Position = 0;
            Transaction decoded = _txDecoder.Decode(rlpStream, RlpBehaviors.UseTransactionTypes);
            decoded!.SenderAddress = new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance).RecoverAddress(decoded);
            decoded.Hash = decoded.CalculateHash();
            decoded.Should().BeEquivalentTo(testCase.Tx, testCase.Description);
        }
    }
}
