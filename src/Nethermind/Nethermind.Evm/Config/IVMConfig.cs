// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.Config;
public interface IVMConfig : IConfig
{
    static string DllName(string dllName) => $"{dllName}.Nethermind.g.c.dll";

    [ConfigItem(
        Description = "Set IL-EVM Activated Mode : 0- Il-evm is turned off 1- Pattern mode, 2- Precompilation mode",
        DefaultValue = "0")]
    public int IlEvmEnabledMode { get; set; }

    [ConfigItem(
        Description = "Threshold for enabling optimizations for a contract",
        DefaultValue = "32")]
    public int IlEvmAnalysisThreshold { get; set; }

    [ConfigItem(
        Description = "Activates or Deactivates aggressive optimizations",
        DefaultValue = "false")]
    public bool IsIlEvmAggressiveModeEnabled { get; set; }

    [ConfigItem(
        Description = "Sets Analysis Queue Max Size",
        DefaultValue = "8")]
    public int IlEvmAnalysisQueueMaxSize { get; set; }

    [ConfigItem(
        Description = "Saves Precompiled Contracts on disk",
        DefaultValue = "false")]
    public bool IlEvmPersistPrecompiledContractsOnDisk { get; set; }

    [ConfigItem(
        Description = "Sets the path for saving Precompiled Contracts",
        DefaultValue = "PrecompiledContracts")]
    public string IlEvmPrecompiledContractsPath { get; set; }

    [ConfigItem(
        Description = "Sets Analysis max running tasks count",
        DefaultValue = "8")]
    public int IlEvmAnalysisMaxTasksCount { get; set; }

    public bool IsVmOptimizationEnabled => IlEvmEnabledMode != ILMode.NO_ILVM;
}
