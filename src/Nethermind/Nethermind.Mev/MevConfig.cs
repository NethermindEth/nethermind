// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Mev
{
    public class MevConfig : IMevConfig
    {
        public static readonly MevConfig Default = new();
        public bool Enabled { get; set; }
        public UInt256 BundleHorizon { get; set; } = 60 * 60;
        public int BundlePoolSize { get; set; } = 200;
        public int MaxMergedBundles { get; set; } = 1;
        public string TrustedRelays { get; set; } = string.Empty;
    }
}
