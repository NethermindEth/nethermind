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
using NUnit.Framework;

namespace Nethermind.Hive.Tests
{
    public class HivePluginTests
    {
        [Test]
        public void Can_create()
        {
            _ = new HivePlugin();
        }
        
        [Test]
        public void Throws_on_null_api_in_init()
        {
            HivePlugin plugin = new();
            Assert.Throws<ArgumentNullException>(() => plugin.Init(null));
        }
        
        [Test]
        public void Can_initialize()
        {
            HivePlugin plugin = new();
            plugin.Init(Runner.Test.Ethereum.Build.ContextWithMocks());
            plugin.InitRpcModules();
        }
    }
}
