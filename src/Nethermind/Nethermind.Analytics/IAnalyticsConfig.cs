// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Analytics
{
    [ConfigCategory(DisabledForCli = true, HiddenFromDocs = true)]
    public interface IAnalyticsConfig : IConfig
    {
        [ConfigItem(Description = "If 'false' then no analytics plugins will be loaded", DefaultValue = "false")]
        public bool PluginsEnabled { get; set; }

        [ConfigItem(Description = "If 'false' then transactions are not streamed by default to gRPC endpoints.", DefaultValue = "false")]
        public bool StreamTransactions { get; set; }

        [ConfigItem(Description = "If 'false' then blocks are not streamed by default to gRPC endpoints.", DefaultValue = "false")]
        public bool StreamBlocks { get; set; }

        [ConfigItem(Description = "If 'true' then all analytics will be also output to logger", DefaultValue = "false")]
        public bool LogPublishedData { get; set; }
    }
}
