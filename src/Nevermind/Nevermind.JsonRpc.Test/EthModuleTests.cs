/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nevermind.Blockchain;
using Nevermind.Core;
using Nevermind.Core.Extensions;
using Nevermind.Json;
using Nevermind.JsonRpc.Module;
using Nevermind.Store;

namespace Nevermind.JsonRpc.Test
{
    [TestClass]
    public class EthModuleTests
    {
        private IEthModule _ethModule;

        [TestInitialize]
        public void Initialize()
        {
            var logger = new ConsoleLogger();
            //_ethModule = new EthModule(logger, new JsonSerializer(logger), new BlockchainProcessor(), new StateProvider() );
        }

        [TestMethod]
        public void GetBalanceSuccessTest()
        {
            var hex = new Hex(1024.ToBigEndianByteArray()).ToString(true, true);
        }
    }
}