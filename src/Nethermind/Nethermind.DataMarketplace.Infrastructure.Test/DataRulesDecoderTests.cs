// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Infrastructure.Test
{
    [TestFixture]
    public class DataRulesDecoderTests
    {
        [Test]
        public void One_and_null()
        {
            DataAssetRuleDecoder.Init();
            DataAssetRules rules = new DataAssetRules(new DataAssetRule(1), null);

            DataAssetRulesDecoder decoder = new DataAssetRulesDecoder();
            decoder.Decode(decoder.Encode(rules).Bytes.AsRlpStream()).Should().BeEquivalentTo(rules);
        }
    }
}
