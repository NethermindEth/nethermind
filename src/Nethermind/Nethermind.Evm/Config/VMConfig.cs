// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis.IL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.Config;
public class VMConfig : IVMConfig
{
    public bool IsILEvmEnabled => IlEvmEnabledMode is not ILMode.NO_ILVM;
    public ILMode IlEvmEnabledMode { get; set; } = ILMode.AOT_MODE;
    public int IlEvmAnalysisThreshold { get; set; } = 32;
    public int IlEvmAnalysisQueueMaxSize { get; set; } = 8;
    public bool IlEvmPersistPrecompiledContractsOnDisk { get; set; } = false;
    public string IlEvmPrecompiledContractsPath { get; set; } = "PrecompiledContracts";
    public int IlEvmContractsPerDllCount { get; set; } = 8;

    public float IlEvmAnalysisCoreUsage { get; set; } = 0.0f;
    public int? IlEvmBytecodeMaxLength { get; set; } = null;
    public int IlEvmBytecodeMinLength { get; set; } = 16;
    public string[] IlEvmAllowedContracts { get; set; } =
    {
        WhitelistedContracts.Weth,
        WhitelistedContracts.UsdcProxy,
        WhitelistedContracts.AllowanceHolder,
        WhitelistedContracts.Erc20Router,
        WhitelistedContracts.Permit2,
        WhitelistedContracts.Uniswap,
        WhitelistedContracts.Gasspas
    };

}
