// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        private static StackList<int> GetStackList() => new() { 1, 2, 5 };
    }
}
