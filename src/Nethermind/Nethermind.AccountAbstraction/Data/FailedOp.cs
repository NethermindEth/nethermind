// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.AccountAbstraction.Data
{
    public readonly struct FailedOp
    {
        public readonly UInt256 _opIndex;
        public readonly Address _paymaster;
        public readonly string _reason;

        public FailedOp(UInt256 opIndex, Address paymaster, string reason)
        {
            _opIndex = opIndex;
            _paymaster = paymaster;
            _reason = reason;
        }

        public override string ToString()
        {
            string type = _paymaster == Address.Zero ? "wallet" : "paymaster";
            return $"{type} simulation failed with reason '{_reason}'";
        }
    }
}
