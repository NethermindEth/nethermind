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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ethereum.Test.Base
{
    public class LegacyFileTestsSource
    {
        private readonly string _fileName;
        private readonly string _wildcard;

        public LegacyFileTestsSource(string fileName, string wildcard = null)
        {
            _fileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            _wildcard = wildcard;
        }

        public IEnumerable<LegacyBlockchainTest> LoadTests()
        {
            try
            {
                if (Path.GetFileName(_fileName).StartsWith("."))
                {
                    return Enumerable.Empty<LegacyBlockchainTest>();
                }

                if (_wildcard != null && !_fileName.Contains(_wildcard))
                {
                    return Enumerable.Empty<LegacyBlockchainTest>();
                }

                string json = File.ReadAllText(_fileName);
                return JsonToBlockchainTest.ConvertLegacy(json);
            }
            catch (Exception e)
            {
                return Enumerable.Repeat(new LegacyBlockchainTest {Name = _fileName, LoadFailure = $"Failed to load: {e.Message}"}, 1);
            }
        }
    }
}