// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Monitoring.Metrics;

namespace Nethermind.Init
{
    public static class Metrics
    {
        [Description("Version number")]
        public static long Version { get; set; }
    }
}
