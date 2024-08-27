// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.Config;
public interface IVMConfig : IConfig
{
    [ConfigItem(
        Description = "Activates or Deactivates n-gram pattern optimizations",
        DefaultValue = "false")]
    public bool IsPatternMatchingEnabled { get; set; }

    [ConfigItem(
        Description = "Activates or Deactivates JIT optimizations",
        DefaultValue = "false")]
    public bool IsJitEnabled { get; set; }

    [ConfigItem(
        Description = "Threshold for enabling JIT optimizations",
        DefaultValue = "128")]
    public int JittingThreshold { get; set; }

    [ConfigItem(
        Description = "Threshold for enabling n-gram pattern optimizations",
        DefaultValue = "32")]
    public int PatternMatchingThreshold { get; set; }

    public bool IsVmOptimizationEnabled => IsPatternMatchingEnabled || IsJitEnabled;
}
