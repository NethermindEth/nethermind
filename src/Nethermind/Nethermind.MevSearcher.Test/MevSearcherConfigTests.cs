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

namespace Nethermind.MevSearcher.Test
{
    [TestFixture]
    public class MevSeacherConfigTests
    {
        [Test]
        public void Can_create()
        {
            _ = new MevSearcherConfig();
        }
        
        [Test]
        public void Disabled_by_default()
        {
            MevSearcherConfig mevConfig = new();
            mevConfig.Enabled.Should().BeFalse();
        }
        
        [Test]
        public void Can_enabled_and_disable()
        {
            MevSearcherConfig mevSearcherConfig = new();
            mevSearcherConfig.Enabled = true;
            mevSearcherConfig.Enabled.Should().BeTrue();
            mevSearcherConfig.Enabled = false;
            mevSearcherConfig.Enabled.Should().BeFalse();
        }
    }
}
