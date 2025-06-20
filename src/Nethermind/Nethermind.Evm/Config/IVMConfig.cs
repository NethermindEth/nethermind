// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Crypto;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;

namespace Nethermind.Evm.Config;
public interface IVMConfig : IConfig
{
    [ConfigItem(
        Description = "Set IL-EVM Activated Mode : 0- Il-evm is turned off 1- Pattern mode, 2- Precompilation mode",
        DefaultValue = "0")]
    public ILMode IlEvmEnabledMode { get; set; }

    [ConfigItem(
        Description = "Threshold for enabling optimizations for a contract",
        DefaultValue = "32")]
    public int IlEvmAnalysisThreshold { get; set; }

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
        Description = "Sets number of contracts to bundle in DLLs",
        DefaultValue = "32")]
    public int IlEvmContractsPerDllCount { get; set; }

    [ConfigItem(
        Description = "Sets percent of threads ILVM can use at max",
        DefaultValue = "0")]
    public float IlEvmAnalysisCoreUsage { get; set; }

    [ConfigItem(
            Description = "Sets Allowed max length of bytecode, if not provided spec.MaxCode is used",
            DefaultValue = "null")]
    public int? IlEvmBytecodeMaxLength { get; set; }

    [ConfigItem(
            Description = "Sets Allowed min length of bytecode",
            DefaultValue = "32")]
    public int IlEvmBytecodeMinLength { get; set; }


    [ConfigItem(
            Description = "Sets allowed contracts",
            DefaultValue = "[]")]
    public string[] IlEvmAllowedContracts { get; set; }

    public bool IsVmOptimizationEnabled => IlEvmEnabledMode != ILMode.NO_ILVM;
}
