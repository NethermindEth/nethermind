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
using System.Collections.Generic;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Core;
using Nethermind.Core.Timers;

namespace Nethermind.AccountAbstraction.Source
{
    public class PaymasterThrottler : IPaymasterThrottler
    {
        public const int TimerHoursSpan = 24;

        public const int TimerMinutesSpan = 0;

        public const int TimerSecondsSpan = 0;

        public readonly uint MinInclusionRateDenominator;

        public const uint ThrottlingSlack = 10;

        public const uint BanSlack = 50;

        private readonly IDictionary<Address, uint> _opsIncluded;

        private readonly IDictionary<Address, uint> _opsSeen;

        private readonly ITimer _timer = new TimerFactory()
            .CreateTimer(new TimeSpan(
                    TimerHoursSpan,
                    TimerMinutesSpan,
                    TimerSecondsSpan
                )
            );

        public PaymasterThrottler() : this(true) { }

        public PaymasterThrottler(bool isBundler)
            : this(isBundler, new Dictionary<Address, uint>(), new Dictionary<Address, uint>()) { }

        public PaymasterThrottler(bool isBundler,
                IDictionary<Address, uint> opsSeen, IDictionary<Address, uint> opsIncluded)
        {
            MinInclusionRateDenominator = isBundler ? (uint)10 : (uint)100;

            _opsSeen = opsSeen;
            _opsIncluded = opsIncluded;

            SetupAndStartTimer();
        }

        public uint IncrementOpsSeen(Address paymaster)
        {
            lock (_opsSeen)
            {
                if (!_opsSeen.ContainsKey(paymaster)) _opsSeen.Add(paymaster, 1);
                else _opsSeen[paymaster]++;

                return _opsSeen[paymaster];
            }
        }

        public uint IncrementOpsIncluded(Address paymaster)
        {
            lock (_opsIncluded)
            {
                if (!_opsIncluded.ContainsKey(paymaster)) _opsIncluded.Add(paymaster, 1);
                else _opsIncluded[paymaster]++;

                return _opsIncluded[paymaster];
            }
        }

        public PaymasterStatus GetPaymasterStatus(Address paymaster)
        {
            if (paymaster == Address.Zero) return PaymasterStatus.Ok;

            uint minExpectedIncluded;

            lock (_opsSeen)
            {
                if (!_opsSeen.ContainsKey(paymaster)) return PaymasterStatus.Ok;
                minExpectedIncluded = FloorDivision(_opsSeen[paymaster], MinInclusionRateDenominator);
            }

            lock (_opsIncluded)
            {
                if (_opsIncluded[paymaster] <= minExpectedIncluded + ThrottlingSlack) return PaymasterStatus.Ok;
                if (_opsIncluded[paymaster] <= minExpectedIncluded + BanSlack) return PaymasterStatus.Throttled;
            }

            return PaymasterStatus.Banned;
        }

        public uint GetPaymasterOpsSeen(Address paymaster)
        {
            lock (_opsSeen)
            {
                return _opsSeen.ContainsKey(paymaster) ? _opsSeen[paymaster] : 0;
            }
        }

        public uint GetPaymasterOpsIncluded(Address paymaster)
        {
            lock (_opsIncluded)
            {
                return _opsIncluded.ContainsKey(paymaster) ? _opsIncluded[paymaster] : 0;
            }
        }

        protected void UpdateUserOperationMaps(object source, EventArgs args)
        {
            lock (_opsSeen)
            {
                foreach (Address paymaster in _opsSeen.Keys)
                {
                    uint correction = FloorDivision(_opsSeen[paymaster], TimerHoursSpan);

                    _opsSeen[paymaster] = _opsSeen[paymaster] >= correction
                        ? _opsSeen[paymaster] - correction
                        : 0;
                }
            }

            lock (_opsIncluded)
            {
                foreach (Address paymaster in _opsIncluded.Keys)
                {
                    uint correction = FloorDivision(_opsIncluded[paymaster], TimerHoursSpan);

                    _opsIncluded[paymaster] = _opsIncluded[paymaster] >= correction
                        ? _opsIncluded[paymaster] - correction
                        : 0;
                }
            }
        }

        private uint FloorDivision(uint dividend, uint divisor)
        {
            if (divisor == 0) throw new Exception("PaymasterThrottler: Divisor cannot be == 0");

            uint remainder = dividend % divisor;
            return (dividend - remainder) / divisor;
        }

        private void SetupAndStartTimer()
        {
            _timer.Elapsed += UpdateUserOperationMaps!;
            _timer.AutoReset = true;
            _timer.Start();
        }
    }
}
