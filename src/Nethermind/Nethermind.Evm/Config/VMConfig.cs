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
    public bool IsILEvmEnabled { get; set; } = false;
    public int IlEvmEnabledMode { get; set; } = 0;
    public int IlEvmAnalysisThreshold { get; set; } = 32;
    public bool IsIlEvmAggressiveModeEnabled { get; set; } = false;
    public int IlEvmAnalysisQueueMaxSize { get; set; } = 8;
}
