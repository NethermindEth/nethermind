// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core2;

namespace Nethermind.HonestValidator.Tests.Helpers
{
    public class FastTestClock : IClock
    {
        private readonly Queue<DateTimeOffset> _timeValues;

        public FastTestClock(IEnumerable<DateTimeOffset> timeValues)
        {
            CompleteWaitHandle = new ManualResetEvent(false);
            _timeValues = new Queue<DateTimeOffset>(timeValues);
        }

        public ManualResetEvent CompleteWaitHandle { get; }

        public DateTimeOffset Now()
        {
            throw new NotSupportedException();
        }

        public DateTimeOffset UtcNow()
        {
            var next = _timeValues.Dequeue();
            if (_timeValues.Count == 0)
            {
                CompleteWaitHandle.Set();
            }
            return next;
        }
    }
}
