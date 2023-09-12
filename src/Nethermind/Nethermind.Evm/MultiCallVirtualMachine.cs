// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Evm;

public struct MultiCallDoNotTraceTransfers { }
public struct MultiCallDoTraceTransfers { }

public class MultiCallVirtualMachine<TTraceTransfers> : VirtualMachine, IMultiCallVirtualMachine

{
    private readonly Dictionary<Address, CodeInfo> _codeOverwrites = new();

    public MultiCallVirtualMachine(IBlockhashProvider? blockhashProvider,
        ISpecProvider? specProvider, ILogManager? logManager) :
        base(blockhashProvider, specProvider, logManager)
    {
    }

    protected override IVirtualMachine CreateVirtualMachine(IBlockhashProvider? blockhashProvider, ISpecProvider? specProvider,
        ILogger? logger)
    {
        bool traceTransfers = typeof(TTraceTransfers) == typeof(MultiCallDoTraceTransfers);


        IVirtualMachine result;
        if (!logger.IsTrace)
        {
            result = new MultiCallVirtualMachineImpl<NotTracing>(traceTransfers, blockhashProvider, specProvider, logger);
        }
        else
        {
            result = new MultiCallVirtualMachineImpl<IsTracing>(traceTransfers, blockhashProvider, specProvider, logger);
        }

        return result;
    }

    public void SetCodeOverwrite(IWorldState worldState, IReleaseSpec vmSpec, Address key, CodeInfo value,
        Address? redirectAddress = null)
    {
        if (redirectAddress != null) _codeOverwrites[redirectAddress] = base.GetCachedCodeInfo(worldState, key, vmSpec);
        _codeOverwrites[key] = value;
    }

    public override CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec)
    {
        return _codeOverwrites.TryGetValue(codeSource, out CodeInfo result)
            ? result
            : base.GetCachedCodeInfo(worldState, codeSource, vmSpec);
    }

}
