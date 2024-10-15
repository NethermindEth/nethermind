// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;

namespace Nethermind.Evm.Tracing.ParityStyle;

[Flags]
public enum ParityTraceTypes
{
    [Description]
    None = 0,
    [Description]
    VmTrace = 1,
    [Description]
    StateDiff = 2,
    [Description]
    Trace = 4,
    [Description]
    Rewards = 8,
    [Description]
    All = 15,
}
