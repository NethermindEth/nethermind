// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Eth.Multicall;

public class MultiCallVirtualMachine : VirtualMachine
{
    private readonly IBlockhashProvider? _blockhashProvider;
    private readonly ISpecProvider? _specProvider;
    private readonly ILogManager? _logManager;

    public MultiCallVirtualMachine(MultiCallVirtualMachine vm) :
        this(vm._blockhashProvider, vm._specProvider, vm._logManager)
    {
        overloaded = vm.overloaded;
    }

    public MultiCallVirtualMachine(IBlockhashProvider? blockhashProvider,
        ISpecProvider? specProvider, ILogManager? logManager) :
        base(blockhashProvider, specProvider, logManager)
    {
        _blockhashProvider = blockhashProvider;
        _specProvider = specProvider;
        _logManager = logManager;
    }

    private Dictionary<Address, CodeInfo> overloaded = new();

    public void SetOverwrite(Address key, CodeInfo value)
    {
        overloaded[key] = value;
    }

    public override CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec)
    {
        return overloaded.TryGetValue(codeSource, out CodeInfo result) ?
            result : base.GetCachedCodeInfo(worldState, codeSource, vmSpec);
    }
}
