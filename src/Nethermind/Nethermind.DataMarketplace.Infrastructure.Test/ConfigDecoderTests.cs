// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Infrastructure.Test
{
    [TestFixture]
    public class NdmConfigDecoderTests
    {
        [Test]
        public void Config_roundtrip()
        {
            NdmConfigDecoder.Init();
            NdmConfig ndmConfig = new NdmConfig();

            NdmConfigDecoder decoder = new NdmConfigDecoder();
            decoder.Decode(decoder.Encode(ndmConfig).Bytes.AsRlpStream()).Should().BeEquivalentTo(ndmConfig);
        }
    }
}
