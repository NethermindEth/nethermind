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

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.JsonRpc;

namespace Nethermind.AccountAbstraction.Source
{
    public class PaymasterThrottler
    {
        internal enum PaymasterStatus { Ok, Throttled, Banned };
        
        public const uint MinInclusionRateDenominator = 100;

        public const uint ThrottlingSlack = 10;

        public const uint BanSlack = 50;

        private readonly IDictionary<Address, uint> _opsSeen;

        private readonly IDictionary<Address, uint> _opsIncluded;

        public PaymasterThrottler()
        {
            _opsSeen = new Dictionary<Address, uint>();
            _opsIncluded = new Dictionary<Address, uint>();
        }
        
        public PaymasterThrottler(
            IDictionary<Address, uint> opsSeen,
            IDictionary<Address, uint> opsIncluded
            )
        {
            _opsSeen = opsSeen;
            _opsIncluded = opsIncluded;
        }

        internal uint GetPaymasterOpsSeen(Address paymaster)
        {
            return (_opsSeen.ContainsKey(paymaster)) ? _opsSeen[paymaster] : 0;
        }

        internal uint GetPaymasterOpsIncluded(Address paymaster)
        {
            return (_opsIncluded.ContainsKey(paymaster)) ? _opsIncluded[paymaster] : 0;
        }

        internal uint IncrementOpsSeen(Address paymaster)
        {
            if (!_opsSeen.ContainsKey(paymaster)) _opsSeen.Add(paymaster, 1);
            else _opsSeen[paymaster]++;

            return _opsSeen[paymaster];
        }

        internal uint IncrementOpsIncluded(Address paymaster)
        {
            if (!_opsIncluded.ContainsKey(paymaster)) _opsIncluded.Add(paymaster, 1);
            else _opsIncluded[paymaster]++;

            return _opsIncluded[paymaster];
        }
        
        //_opsSeen[paymaster] = _opsSeen[paymaster] - _opsSeen[paymaster] // 24
        internal void UpdateUserOperationMaps()
        {
            
        }

        internal PaymasterStatus GetPaymasterStatus(Address paymaster)
        {
            if (!_opsSeen.ContainsKey(paymaster)) return PaymasterStatus.Ok;

            uint minExpectedIncluded = FloorDivision(_opsSeen[paymaster], MinInclusionRateDenominator);

            if (_opsIncluded[paymaster] <= minExpectedIncluded + ThrottlingSlack) return PaymasterStatus.Ok;
            if (_opsIncluded[paymaster] <= minExpectedIncluded + BanSlack) return PaymasterStatus.Throttled;
 
            return PaymasterStatus.Banned;
        }
        
        private uint FloorDivision(uint dividend, uint divisor)
        {
            if (divisor == 0) throw new Exception("PaymasterThrottler: Divisor cannot be == 0");

            uint remainder = dividend % divisor;
            return (dividend - remainder) / divisor;
        }
    }
}
