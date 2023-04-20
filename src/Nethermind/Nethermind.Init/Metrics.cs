// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core;
using Nethermind.Monitoring.Metrics;

namespace Nethermind.Init
{
    public static class Metrics
    {
        [Description("Version number")]
        [MetricsStaticDescriptionTag(nameof(ProductInfo.Version), typeof(ProductInfo))]
        [MetricsStaticDescriptionTag(nameof(ProductInfo.Commit), typeof(ProductInfo))]
        [MetricsStaticDescriptionTag(nameof(ProductInfo.Runtime), typeof(ProductInfo))]
        [MetricsStaticDescriptionTag(nameof(ProductInfo.BuildTimestamp), typeof(ProductInfo))]
        [MetricsStaticDescriptionTag(nameof(ProductInfo.Instance), typeof(ProductInfo))]
        [MetricsStaticDescriptionTag(nameof(ProductInfo.Network), typeof(ProductInfo))]
        public static long Version { get; set; }
    }
}
