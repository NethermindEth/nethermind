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

using System.Linq;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class BloomConverterTests : ConverterTestBase<Bloom>
    {
        [Test]
        public void Null_values()
        {
            TestConverter(null, (bloom, bloom1) => bloom == bloom1, new BloomConverter());
        }
        
        [Test]
        public void Empty_bloom()
        {
            TestConverter(Bloom.Empty, (bloom, bloom1) => bloom.Equals(bloom1), new BloomConverter());
        }
        
        [Test]
        public void Full_bloom()
        {
            TestConverter(
                new Bloom(Enumerable.Range(0, 255).Select(i => (byte)i).ToArray()),
                (bloom, bloom1) => bloom.Equals(bloom1), new BloomConverter());
        }
    }
}
