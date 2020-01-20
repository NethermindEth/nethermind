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

using System;
using Nethermind.Core;

namespace Nethermind.AuRa
{
    public class AuRaStepCalculator : IAuRaStepCalculator
    {
        private readonly int _stepDuration;
        private readonly int _stepDurationMilliseconds;
        private readonly ITimestamper _timestamper;
        
        /// <summary>
        /// 
        /// </summary>
        /// <param name="stepDuration">Step duration in seconds.</param>
        /// <param name="timestamper"></param>
        public AuRaStepCalculator(int stepDuration, ITimestamper timestamper)
        {
            _stepDuration = stepDuration;
            _stepDurationMilliseconds = stepDuration * 1000;
            _timestamper = timestamper;
        }

        public long CurrentStep => _timestamper.EpochSecondsLong / _stepDuration;

        public TimeSpan TimeToNextStep => new TimeSpan(TimeToNextStepInTicks);
        public TimeSpan TimeToStep(long step)
        {
            return new TimeSpan(TimeToNextStep.Ticks + (step - CurrentStep - 1) * _stepDurationMilliseconds * TimeSpan.TicksPerMillisecond);
        }

        private long TimeToNextStepInTicks => (_stepDurationMilliseconds - _timestamper.EpochMillisecondsLong % _stepDurationMilliseconds) * TimeSpan.TicksPerMillisecond;
    }
}