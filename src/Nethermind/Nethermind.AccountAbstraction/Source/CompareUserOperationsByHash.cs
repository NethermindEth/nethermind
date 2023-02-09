// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.AccountAbstraction.Data;

namespace Nethermind.AccountAbstraction.Source
{
    public class CompareUserOperationsByHash : IComparer<UserOperation>
    {
        public static readonly CompareUserOperationsByHash Instance = new();

        private CompareUserOperationsByHash() { }

        public int Compare(UserOperation? x, UserOperation? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;

            return x.RequestId!.CompareTo(y.RequestId!);
        }
    }
}
