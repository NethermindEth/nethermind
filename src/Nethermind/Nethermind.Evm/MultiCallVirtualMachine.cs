// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Evm;

public interface ITraceTransfers { }
public struct MultiCallDoNotTraceTransfers : ITraceTransfers { }
public struct MultiCallDoTraceTransfers : ITraceTransfers { }

public class MultiCallVirtualMachine<TTraceTransfers> : VirtualMachine, IMultiCallVirtualMachine
    where TTraceTransfers : struct, ITraceTransfers
{
    private readonly Dictionary<Address, CodeInfo> _codeOverwrites = new();

    public MultiCallVirtualMachine(IBlockhashProvider? blockhashProvider,
        ISpecProvider? specProvider, ILogManager? logManager) :
        base(blockhashProvider, specProvider, logManager)
    {
    }

    protected override IVirtualMachine CreateVirtualMachine(
        IBlockhashProvider? blockhashProvider,
        ISpecProvider? specProvider,
        ILogger? logger)
    {
        bool traceTransfers = typeof(TTraceTransfers) == typeof(MultiCallDoTraceTransfers);

        IVirtualMachine result = logger?.IsTrace == false
            ? new MultiCallVirtualMachineImpl<NotTracing>(traceTransfers, blockhashProvider, specProvider, logger)
            : new MultiCallVirtualMachineImpl<IsTracing>(traceTransfers, blockhashProvider, specProvider, logger);

        return result;
    }

    public void SetCodeOverwrite(
        IWorldState worldState,
        IReleaseSpec vmSpec,
        Address key,
        CodeInfo value,
        Address? redirectAddress = null)
    {
        if (redirectAddress is not null)
        {
            _codeOverwrites[redirectAddress] = base.GetCachedCodeInfo(worldState, key, vmSpec);
        }

        _codeOverwrites[key] = value;
    }

    public override CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec) =>
        _codeOverwrites.TryGetValue(codeSource, out CodeInfo result)
            ? result
            : base.GetCachedCodeInfo(worldState, codeSource, vmSpec);
}
