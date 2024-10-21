// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;

namespace Nethermind.Evm.Tracing.ParityStyle;

[Flags]
public enum ParityTraceTypes
{
    [Description("None.")]
    None = 0,
    [Description("Virtual Machine execution trace. Provides a full trace of the VM’s state throughout the execution of transactions at each op-code, including for any subcalls.")]
    VmTrace = 1,
    [Description("State difference. Provides information detailing all altered portions of the Ethereum state made due to the execution of transactions.")]
    StateDiff = 2,
    [Description("Transaction trace including subcalls.")]
    Trace = 4,
    [Description("Includes block rewards in trace when tracing full blocks.")]
    Rewards = 8,
    [Description($"Combines the `{nameof(Rewards)}` `{nameof(StateDiff)}` `{nameof(Trace)}` `{nameof(VmTrace)}` options.")]
    All = VmTrace | StateDiff | Trace | Rewards,
}
