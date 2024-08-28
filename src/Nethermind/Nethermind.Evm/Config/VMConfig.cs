// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.Config;
public class VMConfig : IVMConfig
{
    public bool IsPatternMatchingEnabled { get; set; } = false;
    public bool IsJitEnabled { get; set; } = false;
    public int PatternMatchingThreshold { get; set; } = 32;
    public int JittingThreshold { get; set; } = 128;
}
