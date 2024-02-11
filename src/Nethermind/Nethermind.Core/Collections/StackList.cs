// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Collections
{
    public sealed class StackList<T> : List<T> where T : IComparable<T>
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

        public bool TryGetSearchedItem(T activation, out T? item)
        {
            int index = BinarySearch(activation);

            if (index >= 0)
            {
                item = this[index];
                return true;
            }
            else
            {
                int largerIndex = ~index;
                if (largerIndex != 0)
                {
                    item = this[largerIndex - 1];
                    return true;
                }
                else
                {
                    item = default;
                    return false;
                }
            }
        }
    }
}
