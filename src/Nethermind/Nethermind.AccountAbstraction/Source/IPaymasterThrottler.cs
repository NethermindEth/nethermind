// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.AccountAbstraction.Data;
using Nethermind.Core;

namespace Nethermind.AccountAbstraction.Source
{
    public interface IPaymasterThrottler
    {
        public uint IncrementOpsSeen(Address paymaster);
        public uint IncrementOpsIncluded(Address paymaster);
        public PaymasterStatus GetPaymasterStatus(Address paymaster);
    }
}
