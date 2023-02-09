// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Core;
using Nethermind.Core.Timers;

/*
 @title Paymaster Throttler - A manager of the reputation scoring for paymasters under EIP-4337, valid for both 
 bundlers and clients.
 (See https://eips.ethereum.org/EIPS/eip-4337#reputation-scoring-and-throttlingbanning-for-paymasters)
*/

namespace Nethermind.AccountAbstraction.Source
{
    public class PaymasterThrottler : IPaymasterThrottler
    {
        //Timer parameters corresponding to a once an hour hour timespan, for updating the throttler's data.
        public const int TimerHoursSpan = 1;
        public const int TimerMinutesSpan = 0;
        public const int TimerSecondsSpan = 0;

        //A paymaster must include, on average, at least 1 out of every MinInclusionRateDenominator of the transactions
        //it sees to avoid being throttled or banned. This parameter varies among clients and bundlers.
        public readonly uint MinInclusionRateDenominator;

        //Slack parameters, indicating how many included operations can a paymaster fall behind before being punished.
        public const uint ThrottlingSlack = 10;
        public const uint BanSlack = 50;

        //The throttler keeps track of a moving average of the operations seen and included by each paymaster.
        private readonly ConcurrentDictionary<Address, uint> _opsIncluded;
        private readonly ConcurrentDictionary<Address, uint> _opsSeen;

        //Initialize 24-hour timer.
        private readonly ITimer _timer = new TimerFactory()
            .CreateTimer(new TimeSpan(
                    TimerHoursSpan,
                    TimerMinutesSpan,
                    TimerSecondsSpan
                )
            );

        //Constructors for the throttler class. Distinguish between the miner and bundler case, and load preexisting
        //paymaster dictionary data (if applicable)
        public PaymasterThrottler() : this(true) { }

        public PaymasterThrottler(bool isMiner)
            : this(isMiner, new ConcurrentDictionary<Address, uint>(), new ConcurrentDictionary<Address, uint>()) { }

        public PaymasterThrottler(bool isMiner,
            ConcurrentDictionary<Address, uint> opsSeen, ConcurrentDictionary<Address, uint> opsIncluded)
        {
            // MinInclusionRateDenominator is set to 10 for bundlers, 100 for clients.
            MinInclusionRateDenominator = isMiner ? (uint)10 : (uint)100;

            _opsSeen = opsSeen;
            _opsIncluded = opsIncluded;

            SetupAndStartTimer();
        }

        public uint GetOpsSeen(Address paymaster)
        {
            return _opsSeen.GetOrAdd(paymaster, 0);
        }

        public uint GetOpsIncluded(Address paymaster)
        {
            return _opsIncluded.GetOrAdd(paymaster, 0);
        }

        /* @dev Adds a new "seen operation" in the throttler's dictionary for a given paymaster.
         * If the paymaster is not in the throttler's records, include it and start its "seen" count at 1. 
         * @param paymaster: Paymaster to update.
         */
        public uint IncrementOpsSeen(Address paymaster)
        {
            return _opsSeen.AddOrUpdate(paymaster, _ => 1, (_, val) => val + 1);
        }

        /* @dev Adds a new "included operation" in the throttler's dictionary for a given paymaster.
         * If the paymaster is not in the throttler's records, include it and start its "included" count at 1. 
         * @param paymaster: Paymaster to update.
         */
        public uint IncrementOpsIncluded(Address paymaster)
        {
            return _opsIncluded.AddOrUpdate(paymaster, _ => 1, (_, val) => val + 1);
        }

        /* @dev Determines a paymaster's status (OK, throttled, or banned), according to the inclusion rate computed
         * from the throttler's dictionaries.
         * @param paymaster: Paymaster to analyze.
         */
        public PaymasterStatus GetPaymasterStatus(Address paymaster)
        {
            //Zero address is used for UserOps without a paymaster; should always return OK.
            if (paymaster == Address.Zero) return PaymasterStatus.Ok;

            uint minExpectedIncluded;

            //Divide operations seen by MinInclusionRateDenominator, rounded down. It is expected that a paymaster has
            //included at least these many operations.
            if (!_opsSeen.ContainsKey(paymaster)) return PaymasterStatus.Ok;
            minExpectedIncluded = FloorDivision(GetOpsSeen(paymaster), MinInclusionRateDenominator);

            //Check if the paymaster has included the required number of operations, and change status as punishment
            //depending on how far behind it has fallen.
            uint opsIncluded = GetOpsIncluded(paymaster);
            if (opsIncluded <= minExpectedIncluded + ThrottlingSlack) return PaymasterStatus.Ok;
            if (opsIncluded <= minExpectedIncluded + BanSlack) return PaymasterStatus.Throttled;

            return PaymasterStatus.Banned;
        }

        /* @dev Includes a paymaster in the throttler's "seen operations" dictionary if it was not previously there,
         * otherwise does nothing.
         * @param paymaster: Paymaster to include.
         */
        public uint GetPaymasterOpsSeen(Address paymaster)
        {
            return _opsSeen.TryGetValue(paymaster, out uint value) ? value : 0;
        }

        /* @dev Includes a paymaster in the throttler's "included operations" dictionary if it was not previously there,
         * otherwise does nothing.
         * @param paymaster: Paymaster to include.
         */
        public uint GetPaymasterOpsIncluded(Address paymaster)
        {
            return _opsIncluded.TryGetValue(paymaster, out uint value) ? value : 0;
        }

        /* @dev Updates the throttler's dictionaries with an exponential-moving-average (EMA) pattern.
         * This guarantees that older data will be "washed out" and inactive paymasters eventually reset to OK status.
         * (See EIP4337 for details)
         */
        protected void UpdateUserOperationMaps(object source, EventArgs args)
        {
            foreach (Address paymaster in _opsSeen.Keys)
            {
                //Correction to be applied hourly for the EMA.
                uint correction = FloorDivision(_opsSeen[paymaster], 24);

                //If the updated value would fall below zero, set it to zero instead.
                _opsSeen[paymaster] = _opsSeen[paymaster] >= correction
                    ? _opsSeen[paymaster] - correction
                    : 0;
            }

            // Repeat for the other dictionary.
            foreach (Address paymaster in _opsIncluded.Keys)
            {
                uint correction = FloorDivision(_opsIncluded[paymaster], 24);

                _opsIncluded[paymaster] = _opsIncluded[paymaster] >= correction
                    ? _opsIncluded[paymaster] - correction
                    : 0;
            }
        }

        /* @dev Returns the quotient of a division of integers, always rounded down.
         * @param dividend: Dividend, or numerator of the division.
         * @param divisor: Divisor, or denominator of the division.
         */
        private uint FloorDivision(uint dividend, uint divisor)
        {
            if (divisor == 0) throw new Exception("PaymasterThrottler: Divisor cannot be == 0");

            uint remainder = dividend % divisor;
            return (dividend - remainder) / divisor;
        }
        // @dev Periodically ask the throttler to update the exponential moving average, in accordance to the timer.
        private void SetupAndStartTimer()
        {
            _timer.Elapsed += UpdateUserOperationMaps!;
            _timer.AutoReset = true;
            _timer.Start();
        }
    }
}
