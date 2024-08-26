// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.Config;
internal class VMConfig : IVMConfig
{
    public bool IsPatternMatchingEnabled { get; set; } = false;
    public bool IsJitEnabled { get; set; } = false;
    public int EnablePatternMatchingThreshold { get; set; } = 32;
    public int EnableJittingThreshold { get; set; } = 128;
}
