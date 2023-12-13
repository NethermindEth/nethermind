// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Source
{
    public class CompareMevBundleByBlock : IComparer<MevBundle>
    {
        public static readonly CompareMevBundleByBlock Default = new();

        public int Compare(MevBundle? x, MevBundle? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (y is null) return 1;
            if (x is null) return -1;

            return x.BlockNumber.CompareTo(y.BlockNumber);
        }
    }
}
