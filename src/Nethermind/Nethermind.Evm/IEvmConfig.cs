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

    [ConfigItem(
        Description = "Number of executions a contract must reach before its instruction stream is built. Keeps the one-time build off rarely-executed code (block processing) and pays only for hot, repeatedly-executed code (eth_call). Minimum 1.",
        DefaultValue = "16")]
    int StreamInterpreterThreshold { get; set; }
}
