// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.FastBlocks
{
    public abstract class FastBlocksBatch
    {
        private Stopwatch _stopwatch = new();
        private long? _scheduledLastTime;
        private long? _requestSentTime;
        private long? _validationStartTime;
        private long? _waitingStartTime;
        private long? _handlingStartTime;
        private long? _handlingEndTime;

        /// <summary>
        /// We want to make sure that we do not let the queues grow too much.
        /// In order to do that we prioritize batches that are most likely to be added immediately instead of being put to dependencies.
        /// Prioritized batches get the fastest peer allocated. Other batches get the slowest peer allocated (ensuring that the fastest peers are never stolen away)
        /// </summary>
        public bool Prioritized { get; set; }

        public PeerInfo? ResponseSourcePeer { get; set; }

        protected FastBlocksBatch()
        {
            _stopwatch.Start();
            _scheduledLastTime = _stopwatch.ElapsedMilliseconds;
        }

        public void MarkRetry()
        {
            Retries++;
            _scheduledLastTime = _stopwatch.ElapsedMilliseconds;
            _validationStartTime = null;
            _requestSentTime = null;
            _handlingStartTime = null;
            _handlingEndTime = null;
        }

        public void MarkSent()
        {
            _requestSentTime = _stopwatch.ElapsedMilliseconds;

        }

        public void MarkValidation()
        {
            _validationStartTime = _stopwatch.ElapsedMilliseconds;
        }

        public void MarkWaiting()
        {
            _waitingStartTime = _stopwatch.ElapsedMilliseconds;
        }

        public void MarkHandlingStart()
        {
            _handlingStartTime = _stopwatch.ElapsedMilliseconds;
            _validationStartTime ??= _handlingStartTime;
        }

        public void MarkHandlingEnd()
        {
            _handlingEndTime = _stopwatch.ElapsedMilliseconds;
        }

        public int Retries { get; private set; }
        public double? AgeInMs => _stopwatch.ElapsedMilliseconds;
        public double? SchedulingTime
            => (_requestSentTime ?? _stopwatch.ElapsedMilliseconds) - (_scheduledLastTime ?? _stopwatch.ElapsedMilliseconds);
        public double? RequestTime
            => (_validationStartTime ?? _stopwatch.ElapsedMilliseconds) - (_requestSentTime ?? _stopwatch.ElapsedMilliseconds);
        public double? ValidationTime
            => (_waitingStartTime ?? _stopwatch.ElapsedMilliseconds) - (_validationStartTime ?? _handlingStartTime ?? _stopwatch.ElapsedMilliseconds);
        public double? WaitingTime
            => (_handlingStartTime ?? _stopwatch.ElapsedMilliseconds) - (_waitingStartTime ?? _handlingStartTime ?? _stopwatch.ElapsedMilliseconds);
        public double? HandlingTime
            => (_handlingEndTime ?? _stopwatch.ElapsedMilliseconds) - (_handlingStartTime ?? _stopwatch.ElapsedMilliseconds);
        public long? MinNumber { get; set; }
    }
}
