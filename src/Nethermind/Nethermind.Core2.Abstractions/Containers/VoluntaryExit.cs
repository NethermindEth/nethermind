// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
