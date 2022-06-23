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

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Resettables;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections
{
    [Parallelizable]
    public class ResettableHashSetTests
    {
        private const int HashSetElementsCount = 1000;

        [Test]
        public void operates_in_parallel()
        {
            int[] array = new int[HashSetElementsCount];
            ResettableHashSet<int> set = new();
            Parallel.For(0, HashSetElementsCount, (i) =>
            {
                set.Add(i);
                set.Contains(i - 1);
                switch (i % 300)
                {
                    case 195:
                        set.CopyTo(array, 0);
                        break;
                    case 200:
                        set.Reset();
                        break;
                }
            });
        }

        [Test]
        public void all_elements_are_added()
        {
            ResettableHashSet<int> set = new();
            Parallel.For(0, HashSetElementsCount, (i) =>
            {
                set.Add(i);
            });
            set.Count.Should().Be(HashSetElementsCount);
        }
    }
}
