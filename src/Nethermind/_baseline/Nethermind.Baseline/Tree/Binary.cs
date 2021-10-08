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

namespace Nethermind.Baseline.Tree
{
    public partial class BaselineTree
    {
        public static class Binary
        {
            public enum BinarySearchDirection
            {
                Up,
                Down
            }

            public static ulong? Search(ulong left, ulong right, Func<ulong, bool> isLeafFound, BinarySearchDirection direction = BinarySearchDirection.Up)
            {
                if (left > right)
                {
                    return null;
                }

                ulong? result = null;
                while (left < right)
                {
                    ulong index = direction == BinarySearchDirection.Up ? left + (right - left) / 2 : right - (right - left) / 2;
                    if (isLeafFound(index))
                    {
                        result = index;
                        if (direction == BinarySearchDirection.Up)
                        {
                            left = index + 1;
                        }
                        else
                        {
                            right = index - 1;
                        }
                    }
                    else
                    {
                        if (direction == BinarySearchDirection.Up)
                        {
                            right = index;
                        }
                        else
                        {
                            left = index;
                        }
                    }
                }

                if (isLeafFound(left))
                {
                    result = direction == BinarySearchDirection.Up ? left : right;
                }

                return result;
            }
        }
    }
}