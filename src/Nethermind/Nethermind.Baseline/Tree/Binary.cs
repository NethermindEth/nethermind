// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
