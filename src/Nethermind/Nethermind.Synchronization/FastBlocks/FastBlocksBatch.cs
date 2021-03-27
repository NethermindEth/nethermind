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
