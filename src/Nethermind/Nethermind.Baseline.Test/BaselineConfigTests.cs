// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Baseline.Config;
using Nethermind.Config.Test;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    [TestFixture]
    public class BaselineConfigTests : ConfigFileTestsBase
    {
        [Test]
        public void Can_set()
        {
            BaselineConfig config = new BaselineConfig();
            var dbCacheIndexAndFilterBlocks = true;
            uint dbBlockCacheSize = 100;
            uint dbWriteBufferSize = 300;
            uint dbWriteBufferNumber = 3;
            config.Enabled.Should().BeFalse();
            config.Enabled = true;
            config.Enabled.Should().BeTrue();
            config.Enabled = false;
            config.Enabled.Should().BeFalse();

            config.BaselineTreeDbCacheIndexAndFilterBlocks.Should().BeFalse();
            config.BaselineTreeMetadataDbCacheIndexAndFilterBlocks.Should().BeFalse();
            config.BaselineTreeDbCacheIndexAndFilterBlocks = dbCacheIndexAndFilterBlocks;
            config.BaselineTreeMetadataDbCacheIndexAndFilterBlocks = dbCacheIndexAndFilterBlocks;
            config.BaselineTreeDbCacheIndexAndFilterBlocks.Should().BeTrue();
            config.BaselineTreeMetadataDbCacheIndexAndFilterBlocks.Should().BeTrue();

            config.BaselineTreeDbBlockCacheSize = dbBlockCacheSize;
            config.BaselineTreeMetadataDbBlockCacheSize = dbBlockCacheSize;
            config.BaselineTreeDbBlockCacheSize.Should().Be(dbBlockCacheSize);
            config.BaselineTreeMetadataDbBlockCacheSize.Should().Be(dbBlockCacheSize);

            config.BaselineTreeDbWriteBufferSize = dbWriteBufferSize;
            config.BaselineTreeMetadataDbWriteBufferSize = dbWriteBufferSize;
            config.BaselineTreeDbWriteBufferSize.Should().Be(dbWriteBufferSize);
            config.BaselineTreeMetadataDbWriteBufferSize.Should().Be(dbWriteBufferSize);

            config.BaselineTreeDbWriteBufferNumber = dbWriteBufferNumber;
            config.BaselineTreeMetadataDbWriteBufferNumber = dbWriteBufferNumber;
            config.BaselineTreeDbWriteBufferNumber.Should().Be(dbWriteBufferNumber);
            config.BaselineTreeMetadataDbWriteBufferNumber.Should().Be(dbWriteBufferNumber);
        }

        [TestCase("baseline", true)]
        [TestCase("spaceneth", true)]
        [TestCase("^baseline ^spaceneth", false)]
        public void Baseline_is_disabled_by_default(string configWildcard, bool enabled)
        {
            Test<IBaselineConfig, bool>(configWildcard, c => c.Enabled, enabled);
        }

        protected override IEnumerable<string> Configs { get; } = new HashSet<string>
        {
            "ropsten_archive.cfg",
            "ropsten.cfg",
            "rinkeby_archive.cfg",
            "rinkeby.cfg",
            "goerli_archive.cfg",
            "goerli.cfg",
            "kovan.cfg",
            "kovan_archive.cfg",
            "mainnet_archive.cfg",
            "mainnet.cfg",
            "poacore.cfg",
            "poacore_archive.cfg",
            "poacore_validator.cfg",
            "xdai.cfg",
            "xdai_archive.cfg",
            "spaceneth.cfg",
            "spaceneth_persistent.cfg",
            "volta.cfg",
            "volta_archive.cfg",
            "volta.cfg",
            "volta_archive.cfg",
            "energyweb.cfg",
            "energyweb_archive.cfg",
            "baseline.cfg",
            "baseline_ropsten.cfg"
        };
    }
}
