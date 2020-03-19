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
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class VoluntaryExit
    {
        public static readonly VoluntaryExit Zero = new VoluntaryExit(Epoch.Zero, ValidatorIndex.Zero);
        
        /// <summary>
        /// The earliest epoch when voluntary exit can be processed
        /// </summary>
        public Epoch Epoch { get; }
        public ValidatorIndex ValidatorIndex { get; }
        
        public VoluntaryExit(Epoch epoch, ValidatorIndex validatorIndex)
        {
            Epoch = epoch;
            ValidatorIndex = validatorIndex;
        }
        
        public bool Equals(VoluntaryExit? other)
        {
            return other != null 
                   && Epoch == other.Epoch 
                   && ValidatorIndex == other.ValidatorIndex;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is VoluntaryExit other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Epoch, ValidatorIndex);
        }
        
        public override string ToString()
        {
            return $"V:{ValidatorIndex} E:{Epoch}";
        }
    }
}