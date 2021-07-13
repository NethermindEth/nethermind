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
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections
{
    [Parallelizable]
    public class StackListTests
    {
        [Test]
        public void peek_should_return_last_element()
        {
            StackList<int> stack = GetStackList();
            stack.Peek().Should().Be(stack[^1]);
        }
        
        [Test]
        public void try_peek_should_return_last_element()
        {
            StackList<int> stack = GetStackList();
            stack.TryPeek(out int item).Should().Be(true);
            item.Should().Be(stack[^1]);
        }
        
        [Test]
        public void try_peek_should_return_false_if_empty()
        {
            StackList<int> stack = new();
            stack.TryPeek(out _).Should().Be(false);
        }
        
        [Test]
        public void pop_should_remove_last_element()
        {
            StackList<int> stack = GetStackList();
            int expectedElement = stack[^1];
            int count = stack.Count;
            stack.Pop().Should().Be(expectedElement);
            stack.Count.Should().Be(count - 1);
        }
        
        [Test]
        public void try_pop_should_return_last_element()
        {
            StackList<int> stack = GetStackList();
            int expectedElement = stack[^1];
            int count = stack.Count;
            stack.TryPop(out int item).Should().Be(true);
            item.Should().Be(expectedElement);
            stack.Count.Should().Be(count - 1);
        }
        
        [Test]
        public void try_pop_should_return_false_if_empty()
        {
            StackList<int> stack = new();
            stack.TryPop(out _).Should().Be(false);
        }

        private static StackList<int> GetStackList() => new() {1, 2, 5};
    }
}
