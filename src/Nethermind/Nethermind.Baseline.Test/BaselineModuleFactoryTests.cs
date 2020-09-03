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

using System.IO.Abstractions;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Baseline.JsonRpc;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Facade;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    [TestFixture]
    public class BaselineModuleFactoryTests
    {
        [Test]
        public void Can_create_many()
        {
            var txPool = Substitute.For<ITxPool>();
            BaselineModuleFactory factory = new BaselineModuleFactory(
                txPool,
                Substitute.For<IStateReader>(),
                Substitute.For<ILogFinder>(),
                Substitute.For<IBlockFinder>(),
                new AbiEncoder(),
                NullWallet.Instance, 
                MainnetSpecProvider.Instance,
                Substitute.For<IFileSystem>(),
                LimboLogs.Instance);

            var a = factory.Create();
            var b = factory.Create();

            a.Should().NotBe(b);
        }
    }
}