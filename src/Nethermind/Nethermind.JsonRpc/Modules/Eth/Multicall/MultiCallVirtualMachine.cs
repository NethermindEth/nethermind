// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Eth.Multicall;

public class MultiCallVirtualMachine : VirtualMachine
{
    private readonly IBlockhashProvider? _blockhashProvider;
    private readonly ISpecProvider? _specProvider;
    private readonly ILogManager? _logManager;
    private readonly Dictionary<Address, CodeInfo> _overloaded = new();

    public MultiCallVirtualMachine(MultiCallVirtualMachine vm) :
        this(vm._blockhashProvider, vm._specProvider, vm._logManager)
    {
        _overloaded = vm._overloaded;
    }

    public MultiCallVirtualMachine(IBlockhashProvider? blockhashProvider,
        ISpecProvider? specProvider, ILogManager? logManager) :
        base(blockhashProvider, specProvider, logManager)
    {
        _blockhashProvider = blockhashProvider;
        _specProvider = specProvider;
        _logManager = logManager;
    }


    public void SetOverwrite(IWorldState worldState, IReleaseSpec vmSpec, Address key, CodeInfo value, Address? redirectAddress = null)
    {
        if (redirectAddress != null)
        {
            _overloaded[redirectAddress] = base.GetCachedCodeInfo(worldState, key, vmSpec);
        }
        _overloaded[key] = value;
    }

    public override CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec)
    {
        if (_overloaded.TryGetValue(codeSource, out CodeInfo result))
            return result;
        else
            return base.GetCachedCodeInfo(worldState, codeSource, vmSpec);
    }
}
