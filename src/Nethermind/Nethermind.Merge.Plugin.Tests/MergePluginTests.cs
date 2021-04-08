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

using Nethermind.Db;
using NUnit.Framework;
using Nethermind.Runner.Test.Ethereum;
using NSubstitute;

namespace Nethermind.Merge.Plugin.Tests
{
    public class MergePluginTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void Init_merge_plugin_does_not_throw_exception(bool enabled)
        {
            MergeConfig mergeConfig = new() {Enabled = enabled};
            Runner.Ethereum.Api.NethermindApi context = Build.ContextWithMocks();
            context.ConfigProvider.GetConfig<IMergeConfig>().Returns(mergeConfig);
            context.MemDbFactory = new MemDbFactory();
            MergePlugin plugin = new();
            Assert.DoesNotThrowAsync(async () => { await plugin.Init(context); });
            Assert.DoesNotThrow(() => { plugin.InitNetworkProtocol(); });
            Assert.DoesNotThrowAsync(async () => { await plugin.InitRpcModules(); });
            Assert.DoesNotThrowAsync(async () => { await plugin.DisposeAsync(); });
        }
    }
}
