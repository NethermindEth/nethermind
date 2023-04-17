// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Source
{
    public class CompareMevBundleByMinTimestamp : IComparer<MevBundle>
    {
        public static readonly CompareMevBundleByMinTimestamp Default = new();

        public int Compare(MevBundle? x, MevBundle? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;

            return x.MinTimestamp.CompareTo(y.MinTimestamp);
        }
    }
}
