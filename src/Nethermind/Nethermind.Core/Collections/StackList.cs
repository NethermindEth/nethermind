// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Collections
{
    public class StackList<T> : List<T>
    {
        public T Peek() => this[^1];

        public bool TryPeek(out T? item)
        {
            if (Count > 0)
            {
                item = Peek();
                return true;
            }
            else
            {
                item = default;
                return false;
            }
        }

        public T Pop()
        {
            T value = this[^1];
            RemoveAt(Count - 1);
            return value;
        }

        public bool TryPop(out T? item)
        {
            if (Count > 0)
            {
                item = Pop();
                return true;
            }
            else
            {
                item = default;
                return false;
            }
        }

        public void Push(T item)
        {
            Add(item);
        }
    }
}
