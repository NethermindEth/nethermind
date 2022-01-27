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
