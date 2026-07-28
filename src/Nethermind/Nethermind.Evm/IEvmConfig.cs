// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Evm;

public interface IEvmConfig : IConfig
{
    [ConfigItem(
        Description = "Whether to compile hot bytecode segments to IL at runtime (IL-EVM tiering). Experimental.",
        DefaultValue = "false")]
    bool IlEvm { get; set; }

    [ConfigItem(
        Description = "Number of executions of a contract's code before its compilable segments are compiled.",
        DefaultValue = "16")]
    int IlEvmThreshold { get; set; }
}
