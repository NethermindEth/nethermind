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

using System;
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
            RemoveAt(Count -1);
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
