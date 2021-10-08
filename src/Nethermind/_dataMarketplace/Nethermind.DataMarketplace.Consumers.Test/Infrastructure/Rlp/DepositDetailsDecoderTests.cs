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

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure.Rlp
{
    [TestFixture]
    public class DepositDetailsDecoderTests
    {
        static DepositDetailsDecoderTests()
        {
            if (_cases == null)
            {
                Deposit deposit = new Deposit(TestItem.KeccakA, 100, 100, 100);
                DataAssetProvider provider = new DataAssetProvider(TestItem.AddressA, "provider");
                DataAsset dataAsset = new DataAsset(TestItem.KeccakA, "data_asset", "desc", 1, DataAssetUnitType.Time, 1000, 10000, new DataAssetRules(new DataAssetRule(1), null), provider, null, QueryType.Stream, DataAssetState.Published, null, false, null);
                _cases = new List<DepositDetails>();
                _cases.Add(new DepositDetails(
                    deposit,
                    dataAsset,
                    TestItem.AddressA,
                    Array.Empty<byte>(),
                    10,
                    Array.Empty<TransactionInfo>(),
                    9,
                    false,
                    false,
                    null,
                    Array.Empty<TransactionInfo>(),
                    false,
                    false,
                    null,
                    0,
                    6));

                _cases.Add(new DepositDetails(
                    deposit,
                    dataAsset,
                    TestItem.AddressA,
                    Array.Empty<byte>(),
                    10,
                    Array.Empty<TransactionInfo>(),
                    9,
                    false,
                    false,
                    null,
                    Array.Empty<TransactionInfo>(),
                    false,
                    false,
                    null,
                    0,
                    6));

                _cases.Add(new DepositDetails(
                    deposit,
                    dataAsset,
                    TestItem.AddressA,
                    Array.Empty<byte>(),
                    10,
                    Array.Empty<TransactionInfo>(),
                    9,
                    false,
                    false,
                    null,
                    Array.Empty<TransactionInfo>(),
                    false,
                    false,
                    null,
                    0,
                    6));
            }
        }

        private static List<DepositDetails> _cases;

        public static IEnumerable<DepositDetails> TestCaseSource()
        {
            return _cases;
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void Roundtrip(DepositDetails item)
        {
            DepositDecoder.Init();
            TransactionInfoDecoder.Init();
            DataAssetDecoder.Init();
            DataAssetRuleDecoder.Init();
            DataAssetRulesDecoder.Init();
            DataAssetProviderDecoder.Init();
            EarlyRefundTicketDecoder.Init();

            DepositDetailsDecoder decoder = new DepositDetailsDecoder();
            decoder.Decode(decoder.Encode(item).Bytes.AsRlpStream()).Should().BeEquivalentTo(item);
        }
    }
}