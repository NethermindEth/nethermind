// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Mev
{
    public interface IMevConfig : IConfig
    {
        [ConfigItem(
            Description = "Defines whether the MEV bundles are allowed.",
            DefaultValue = "false")]
        bool Enabled { get; set; }

        [ConfigItem(
            Description = "Defines how long MEV bundles will be kept in memory by clients",
            DefaultValue = "3600")]
        UInt256 BundleHorizon { get; set; }

        [ConfigItem(
            Description = "Defines the maximum number of MEV bundles that can be kept in memory by clients",
            DefaultValue = "200")]
        int BundlePoolSize { get; set; }

        [ConfigItem(Description = "Defines the maximum number of MEV bundles to be included within a single block", DefaultValue = "1")]
        int MaxMergedBundles { get; set; }

        [ConfigItem(Description = "Defines the list of trusted relay addresses to receive megabundles from as a comma separated string",
            DefaultValue = "")]
        string TrustedRelays { get; set; }
    }

    public static class MevConfigExtensions
    {
        public static IEnumerable<Address> GetTrustedRelayAddresses(this IMevConfig mevConfig) =>
            mevConfig.TrustedRelays
                .Split(",")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct()
                .Select(s => new Address(s));
    }
}
