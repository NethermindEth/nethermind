/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;

namespace Nethermind.Blockchain.Synchronization
{
    public class MeasuredProgress
    {
        public void Update(long value)
        {
            if (!UtcStartTime.HasValue)
            {
                UtcStartTime = DateTime.UtcNow;
                StartValue = value;
            }

            CurrentValue = value;
        }

        public void SetMeasuringPoint()
        {
            if (UtcStartTime != null)
            {
                LastMeasurement = DateTime.UtcNow;
                LastValue = CurrentValue;
            }
        }

        public bool HasEnded => UtcEndTime.HasValue;
        
        public void MarkEnd()
        {
            if (!UtcEndTime.HasValue)
            {
                UtcEndTime = DateTime.UtcNow;
            }
        }

        private long StartValue { get; set; }

        private DateTime? UtcStartTime { get; set; }
        
        private DateTime? UtcEndTime { get; set; }
        
        private DateTime? LastMeasurement { get; set; }

        private long LastValue { get; set; }

        public long CurrentValue { get; private set; }

        private TimeSpan Elapsed => (UtcEndTime ?? DateTime.UtcNow) - (UtcStartTime ?? DateTime.MinValue);

        public decimal TotalPerSecond
        {
            get
            {
                decimal timePassed = (decimal)Elapsed.TotalSeconds;
                if (timePassed == 0M)
                {
                    return 0M;
                }
                
                return (CurrentValue - StartValue) / timePassed;        
            }
        }
            
        public decimal CurrentPerSecond
        {
            get
            {
                if (UtcEndTime != null)
                {
                    return 0;
                }
                
                decimal timePassed = (decimal)(DateTime.UtcNow - (LastMeasurement ?? DateTime.MinValue)).TotalSeconds;
                if (timePassed == 0M)
                {
                    return 0M;
                }
                
                return (CurrentValue - LastValue) / timePassed;        
            }
        }
    }
}