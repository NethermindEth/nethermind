// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Evm;

public interface IEvmConfig : IConfig
{
    [ConfigItem(
        Description = "Whether to execute non-tracing frames on tip forks over the preprocessed instruction stream (per-block static gas precharge, pre-decoded push constants, fused superinstructions, static jumps) instead of the bytecode loop.",
        DefaultValue = "false")]
    bool StreamInterpreter { get; set; }
}
