// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.FastBlocks
{
    public abstract class FastBlocksBatch : IDisposable
    {
        private readonly Stopwatch _stopwatch = new();
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

        /// <summary>
        /// Marks the start of validation phase. This method is optional - if not called,
        /// validation start time will be automatically set when <see cref="MarkHandlingStart"/> is called.
        /// </summary>
        public void MarkValidation()
        {
            _validationStartTime = _stopwatch.ElapsedMilliseconds;
        }

        public void MarkWaiting()
        {
            _waitingStartTime = _stopwatch.ElapsedMilliseconds;
        }

        /// <summary>
        /// Marks the start of handling phase. If <see cref="MarkValidation"/> was not called previously,
        /// validation start time is automatically set to the handling start time, treating validation as part of handling.
        /// </summary>
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
        /// <summary>
        /// Time spent in validation phase. If <see cref="MarkValidation"/> was not called,
        /// this measures time from handling start (or current time if waiting hasn't started).
        /// </summary>
        public double? ValidationTime
            => (_waitingStartTime ?? _stopwatch.ElapsedMilliseconds) - (_validationStartTime ?? _handlingStartTime ?? _stopwatch.ElapsedMilliseconds);
        public double? WaitingTime
            => (_handlingStartTime ?? _stopwatch.ElapsedMilliseconds) - (_waitingStartTime ?? _handlingStartTime ?? _stopwatch.ElapsedMilliseconds);
        public double? HandlingTime
            => (_handlingEndTime ?? _stopwatch.ElapsedMilliseconds) - (_handlingStartTime ?? _stopwatch.ElapsedMilliseconds);

        /// Minimum head number for peer to be allocated
        public abstract long? MinNumber { get; }
        public virtual void Dispose() { }
    }
}
