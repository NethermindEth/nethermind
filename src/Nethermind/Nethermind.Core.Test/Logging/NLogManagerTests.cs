﻿/*
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

using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test.Logging
{
    [TestFixture]
    public class NLogManagerTests
    {
        [Test]
        public void Logger_name_is_set_to_full_class_name()
        {
            NLogManager manager = new NLogManager("test", null);
            NLogLogger logger = (NLogLogger)manager.GetClassLogger();
            Assert.AreEqual(GetType().FullName.Replace("Nethermind.", string.Empty), logger.Logger.Name);
        }
    }
}