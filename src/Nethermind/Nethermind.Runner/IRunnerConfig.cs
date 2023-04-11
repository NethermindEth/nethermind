// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Runner;

public interface IRunnerConfig : IConfig
{
    [ConfigItem(Description = "Set max heap size", DefaultValue = "null")]
    long? MaxHeapMb { get; set; }

    [ConfigItem(Description = "", DefaultValue = "false", HiddenFromDocs = true)]
    bool ShouldWrapInRunner => MaxHeapMb != null;
}
