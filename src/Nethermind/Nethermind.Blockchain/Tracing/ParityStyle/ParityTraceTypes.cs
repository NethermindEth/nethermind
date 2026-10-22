// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

[Flags]
public enum ParityTraceTypes
{
    [Description("None.")]
    None = 0,
    [Description("Provides a full trace of the EVM state throughout the execution of transactions at each op-code, including subcalls.")]
    VmTrace = 1,
    [Description("Provides Ethereum state difference detailing all altered portions of the state made due to the execution of transactions.")]
    StateDiff = 2,
    [Description("Provides transaction trace, including subcalls.")]
    Trace = 4,
    [Description("Includes block rewards in the trace when tracing full blocks.")]
    Rewards = 8,
    [Description($"Combines the `{nameof(Rewards)}` `{nameof(StateDiff)}` `{nameof(Trace)}` `{nameof(VmTrace)}` options.")]
    All = VmTrace | StateDiff | Trace | Rewards,
}
