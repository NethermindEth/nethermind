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
    public bool IsPartialAotEnabled { get; set; } = false;
    public int PatternMatchingThreshold { get; set; } = 32;
    public int PartialAotThreshold { get; set; } = 128;
    public bool AggressivePartialAotMode { get; set; } = false;
    public bool BakeInTracingInAotModes { get; set; } = false;
    public int AnalysisQueueMaxSize { get; set; } = 8;
    public bool IsFullAotEnabled { get; set; } = false;
    public int FullAotThreshold { get; set; } = 256;
}
