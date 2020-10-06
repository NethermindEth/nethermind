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
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class PermissionTxComparer : PermissionTxComparerBase
    {
        private readonly Func<Transaction, bool> _isWhiteListed;
        private readonly Func<Transaction, UInt256> _getPriority;

        public PermissionTxComparer(Func<Transaction, bool> isWhiteListed, Func<Transaction, UInt256> getPriority)
        {
            _isWhiteListed = isWhiteListed ?? throw new ArgumentNullException(nameof(isWhiteListed));
            _getPriority = getPriority ?? throw new ArgumentNullException(nameof(getPriority));
        }

        protected override bool IsWhiteListed(Transaction tx) => _isWhiteListed(tx);

        protected override UInt256 GetPriority(Transaction tx) => _getPriority(tx);
    }
}
