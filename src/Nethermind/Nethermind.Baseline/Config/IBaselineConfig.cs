// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Baseline.Config
{
    [ConfigCategory(Description = "Configuration of the Baseline Protocol integration with Nethermind")]
    public interface IBaselineConfig : IConfig
    {
        [ConfigItem(Description = "If 'true' then the Baseline Module is enabled via JSON RPC", DefaultValue = "false")]
        bool Enabled { get; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "false")]
        bool BaselineTreeDbCacheIndexAndFilterBlocks { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "1024")]
        ulong BaselineTreeDbBlockCacheSize { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "1024")]
        ulong BaselineTreeDbWriteBufferSize { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "4")]
        uint BaselineTreeDbWriteBufferNumber { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "false")]
        bool BaselineTreeMetadataDbCacheIndexAndFilterBlocks { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "1024")]
        ulong BaselineTreeMetadataDbBlockCacheSize { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "1024")]
        ulong BaselineTreeMetadataDbWriteBufferSize { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "4")]
        uint BaselineTreeMetadataDbWriteBufferNumber { get; set; }
    }
}
