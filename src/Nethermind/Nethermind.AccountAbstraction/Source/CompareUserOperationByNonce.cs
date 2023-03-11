// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.AccountAbstraction.Data;

namespace Nethermind.AccountAbstraction.Source
{
    public class CompareUserOperationByNonce : IComparer<UserOperation?>
    {
        public static readonly CompareUserOperationByNonce Instance = new();

        private CompareUserOperationByNonce() { }

        public int Compare(UserOperation? x, UserOperation? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;
            return x.Nonce.CompareTo(y.Nonce);
        }
    }
}
