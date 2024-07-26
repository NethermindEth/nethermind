// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    [Flags]
    public enum ParityTraceTypes
    {
        None = 0,
        VmTrace = 1,
        StateDiff = 2,
        Trace = 4,
        Rewards = 8,
        All = 15,
    }
}
