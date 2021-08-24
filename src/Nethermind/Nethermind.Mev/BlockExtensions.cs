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

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Mev.Data;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Mev
{
    public static class BlockExtensions
    {
        public static bool IsBundleIncluded(this Block currentBlock, MevBundle mevBundle) => 
            currentBlock.Transactions.ContainsUniqueSubsequence(mevBundle.Transactions, ByHashTxComparer.Instance);

        private static bool ContainsUniqueSubsequence<T>(this IEnumerable<T> parent, IEnumerable<T> target, IEqualityComparer<T> equalityComparer)
        {
            bool foundOneMatch = false;
            using IEnumerator<T> parentEnum = parent.GetEnumerator();
            using IEnumerator<T> targetEnum = target.GetEnumerator();
            
            // Get the first target instance; empty sequences are trivially contained
            if (!targetEnum.MoveNext())
                return true;

            while (parentEnum.MoveNext())
            {
                if (equalityComparer.Equals(targetEnum.Current, parentEnum.Current))
                {
                    // Match, so move the target enum forward
                    foundOneMatch = true;
                    if (!targetEnum.MoveNext())
                    {
                        // We went through the entire target, so we have a match
                        return true;
                    }
                }
                else if (foundOneMatch)
                {
                    return false;
                }
            }

            return false;
        }
    }
}
