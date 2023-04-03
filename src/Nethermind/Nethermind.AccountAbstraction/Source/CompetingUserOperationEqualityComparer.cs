// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.AccountAbstraction.Data;

namespace Nethermind.AccountAbstraction.Source
{
    public class CompetingUserOperationEqualityComparer : IEqualityComparer<UserOperation?>
    {
        public static readonly CompetingUserOperationEqualityComparer Instance = new();

        private CompetingUserOperationEqualityComparer() { }

        public bool Equals(UserOperation? x, UserOperation? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (ReferenceEquals(x, null)) return false;
            if (ReferenceEquals(y, null)) return false;
            if (x.GetType() != y.GetType()) return false;
            return x.Sender.Equals(y.Sender) && x.Nonce.Equals(y.Nonce);
        }

        public int GetHashCode(UserOperation obj)
        {
            return HashCode.Combine(obj.Sender, obj.Nonce);
        }
    }
}
