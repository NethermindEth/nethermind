// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections
{
    public sealed class StackList : List<int>
    {
        public int Peek() => this[^1];

        public bool TryPeek(out int item)
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

        public int Pop()
        {
            int value = this[^1];
            RemoveAt(Count - 1);
            return value;
        }

        public bool TryPop(out int item)
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

        public void Push(int item)
        {
            Add(item);
        }

        public bool TryGetSearchedItem(int activation, out int item)
        {
            Span<int> span = CollectionsMarshal.AsSpan(this);
            int index = span.BinarySearch(activation);
            bool result;
            if ((uint)index < (uint)span.Length)
            {
                item = span[index];
                result = true;
            }
            else
            {
                index = ~index - 1;
                if ((uint)index < (uint)span.Length)
                {
                    item = span[index];
                    result = true;
                }
                else
                {
                    item = default;
                    result = false;
                }
            }

            return result;
        }
    }
}
