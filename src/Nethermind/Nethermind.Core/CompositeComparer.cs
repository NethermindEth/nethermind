// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Nethermind.Core.Tests")]

namespace Nethermind.Core
{
    public class CompositeComparer<T> : IComparer<T>
    {
        internal readonly List<IComparer<T>> _comparers;

        public CompositeComparer(params IComparer<T>[] comparers) : this((IEnumerable<IComparer<T>>)comparers)
        {
        }

        public CompositeComparer(IEnumerable<IComparer<T>> comparers)
        {
            _comparers = new List<IComparer<T>>(comparers);
        }

        public CompositeComparer<T> FirstBy(IComparer<T> comparer)
        {
            switch (comparer)
            {
                case CompositeComparer<T> compositeComparer:
                    return new CompositeComparer<T>(compositeComparer._comparers.Concat(_comparers));
                default:
                    return new CompositeComparer<T>(new[] { comparer }.Concat(_comparers));
            }
        }

        public CompositeComparer<T> ThenBy(IComparer<T> comparer)
        {
            switch (comparer)
            {
                case CompositeComparer<T> compositeComparer:
                    return new CompositeComparer<T>(_comparers.Concat(compositeComparer._comparers));
                default:
                    return new CompositeComparer<T>(_comparers.Concat(new[] { comparer }));
            }
        }


        public int Compare(T? x, T? y)
        {
            int result = 0;
            for (int i = 0; i < _comparers.Count; i++)
            {
                result = _comparers[i].Compare(x, y);
                if (result != 0) return result;
            }

            return result;
        }

        public override string ToString() => $"{base.ToString()} [{string.Join(", ", _comparers)}]";
    }

    public static class CompositeComparerExtensions
    {
        public static CompositeComparer<T> ThenBy<T>(this IComparer<T> comparer, IComparer<T> secondComparer)
        {
            if (comparer is CompositeComparer<T> compositeComparer)
            {
                return compositeComparer.ThenBy(secondComparer);
            }
            else if (secondComparer is CompositeComparer<T> secondCompositeComparer)
            {
                return secondCompositeComparer.FirstBy(comparer);
            }
            else
            {
                return new CompositeComparer<T>(comparer, secondComparer);
            }
        }
    }
}
