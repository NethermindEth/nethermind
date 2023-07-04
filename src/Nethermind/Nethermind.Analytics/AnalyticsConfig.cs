// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Analytics
{
    public class AnalyticsConfig : IAnalyticsConfig
    {
        public bool PluginsEnabled { get; set; }
        public bool StreamTransactions { get; set; }
        public bool StreamBlocks { get; set; }
        public bool LogPublishedData { get; set; }
    }
}
