// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            Assert.That(stack.Peek(), Is.EqualTo(stack[^1]));
        }

        [Test]
        public void try_peek_should_return_last_element()
        {
            StackList<int> stack = GetStackList();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stack.TryPeek(out int item), Is.EqualTo(true));
                Assert.That(item, Is.EqualTo(stack[^1]));
            }
        }

        [Test]
        public void try_peek_should_return_false_if_empty()
        {
            StackList<int> stack = [];
            Assert.That(stack.TryPeek(out _), Is.EqualTo(false));
        }

        [Test]
        public void pop_should_remove_last_element()
        {
            StackList<int> stack = GetStackList();
            int expectedElement = stack[^1];
            int count = stack.Count;
            Assert.That(stack.Pop(), Is.EqualTo(expectedElement));
            Assert.That(stack.Count, Is.EqualTo(count - 1));
        }

        [Test]
        public void try_pop_should_return_last_element()
        {
            StackList<int> stack = GetStackList();
            int expectedElement = stack[^1];
            int count = stack.Count;
            Assert.That(stack.TryPop(out int item), Is.EqualTo(true));
            Assert.That(item, Is.EqualTo(expectedElement));
            Assert.That(stack.Count, Is.EqualTo(count - 1));
        }

        [Test]
        public void try_pop_should_return_false_if_empty()
        {
            StackList<int> stack = [];
            Assert.That(stack.TryPop(out _), Is.EqualTo(false));
        }

        private static StackList<int> GetStackList() => [1, 2, 5];
    }
}
