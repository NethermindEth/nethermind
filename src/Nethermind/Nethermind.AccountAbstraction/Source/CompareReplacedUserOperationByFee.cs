// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Int256;

namespace Nethermind.AccountAbstraction.Source
{
    public class CompareReplacedUserOperationByFee : IComparer<UserOperation?>
    {
        public static readonly CompareReplacedUserOperationByFee Instance = new();

        // To replace old user operation, new user operation needs to have fee higher by at least 10% (1/10) of current fee.
        // It is required to avoid acceptance and propagation of user operation with almost the same fee as replaced one.
        private const int PartOfFeeRequiredToIncrease = 10;

        private CompareReplacedUserOperationByFee() { }

        public int Compare(UserOperation? x, UserOperation? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;

            y.MaxFeePerGas.Divide(PartOfFeeRequiredToIncrease, out UInt256 bumpMaxFeePerGas);
            if (y.MaxFeePerGas + bumpMaxFeePerGas > x.MaxFeePerGas) return 1;

            y.MaxPriorityFeePerGas.Divide(PartOfFeeRequiredToIncrease, out UInt256 bumpMaxPriorityFeePerGas);
            return (y.MaxPriorityFeePerGas + bumpMaxPriorityFeePerGas).CompareTo(x.MaxPriorityFeePerGas);
        }
    }
}
