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

using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    public class RocksDbSettingsTests
    {
        [Test]
        public void clone_test()
        {
            RocksDbSettings settings = new("Name", "Path")
            {
                BlockCacheSize = 1,
                UpdateReadMetrics = () => { },
                UpdateWriteMetrics = () => { },
                WriteBufferNumber = 5,
                WriteBufferSize = 10,
                CacheIndexAndFilterBlocks = true
            };

            RocksDbSettings settings2 = settings.Clone("Name2", "Path2");
            settings2.Should().BeEquivalentTo(settings, 
                o => o.Excluding(s => s.DbName).Excluding(s => s.DbPath));
            settings2.DbName.Should().Be("Name2");
            settings2.DbPath.Should().Be("Path2");
        }
    }
}
