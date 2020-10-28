//  Copyright (c) 2018 Demerzel Solutions Limited
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

namespace Nethermind.Core
{
    public class CompositeComparer<T> : IComparer<T>
    {
        private readonly IList<IComparer<T>> _comparers;

        public CompositeComparer(params IComparer<T>[] comparers)
        {
            _comparers = new List<IComparer<T>>(comparers);
        }
        
        public CompositeComparer<T> FirstBy(IComparer<T> comparer)
        {
            _comparers.Insert(0, comparer);
            return this;
        }

        public CompositeComparer<T> ThenBy(IComparer<T> comparer)
        {
            _comparers.Add(comparer);
            return this;
        }
        
        public int Compare(T x, T y)
        {
            int result = 0;
            for (int i = 0; i < _comparers.Count; i++)
            {
                result = _comparers[i].Compare(x, y);
                if (result != 0) return result;
            }

            return result;
        }
    }

    public static class CompositeComparerExtensions
    {
        public static CompositeComparer<T> ThenBy<T>(this IComparer<T> comparer, IComparer<T> secondComparer)
        {
            if (comparer is CompositeComparer<T> compositeComparer)
            {
                compositeComparer.ThenBy(secondComparer);
                return compositeComparer;
            }
            else if (secondComparer is CompositeComparer<T> secondCompositeComparer)
            {
                secondCompositeComparer.FirstBy(comparer);
                return secondCompositeComparer;
            }
            else
            {
                return new CompositeComparer<T>(comparer, secondComparer);
            }
        }
    }
}
