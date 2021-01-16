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
            config.BaselineTreeDbCacheIndexAndFilterBlocks.Should().Equals(dbBlockCacheSize);
            config.BaselineTreeMetadataDbCacheIndexAndFilterBlocks.Should().Equals(dbBlockCacheSize);

            config.BaselineTreeDbWriteBufferSize = dbWriteBufferSize;
            config.BaselineTreeMetadataDbWriteBufferSize = dbWriteBufferSize;
            config.BaselineTreeDbWriteBufferSize.Should().Equals(dbWriteBufferSize);
            config.BaselineTreeMetadataDbWriteBufferSize.Should().Equals(dbWriteBufferSize);

            config.BaselineTreeDbWriteBufferNumber = dbWriteBufferNumber;
            config.BaselineTreeMetadataDbWriteBufferNumber = dbWriteBufferNumber;
            config.BaselineTreeDbWriteBufferNumber.Should().Equals(dbWriteBufferNumber);
            config.BaselineTreeMetadataDbWriteBufferNumber.Should().Equals(dbWriteBufferNumber);
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
            "ropsten_beam.cfg",
            "ropsten.cfg",
            "rinkeby_archive.cfg",
            "rinkeby_beam.cfg",
            "rinkeby.cfg",
            "goerli_archive.cfg",
            "goerli_beam.cfg",
            "goerli.cfg",
            "kovan.cfg",
            "kovan_archive.cfg",
            "mainnet_archive.cfg",
            "mainnet_beam.cfg",
            "mainnet.cfg",
            "sokol.cfg",
            "sokol_archive.cfg",
            "sokol_validator.cfg",
            "poacore.cfg",
            "poacore_archive.cfg",
            "poacore_beam.cfg",
            "poacore_validator.cfg",
            "xdai.cfg",
            "xdai_archive.cfg",
            "xdai_validator.cfg",
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
