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
    [Description("Traces EVM data.")]
    VmTrace = 1,
    [Description("Traces the state and storage.")]
    StateDiff = 2,
    [Description("Traces actions and block receipts.")]
    Trace = 4,
    [Description("Traces block rewards.")]
    Rewards = 8,
    [Description($"Combines the `{nameof(Rewards)}` `{nameof(StateDiff)}` `{nameof(Trace)}` `{nameof(VmTrace)}` options.")]
    All = VmTrace | StateDiff | Trace | Rewards,
}
