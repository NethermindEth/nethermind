// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Evm;

internal class MultiCallVirtualMachineImpl<TLogger> : VirtualMachine<TLogger>
    where TLogger : struct, VirtualMachine.IIsTracing
{
    private readonly bool _traceTransfers;

    public MultiCallVirtualMachineImpl(bool traceTransfers, IBlockhashProvider? blockhashProvider, ISpecProvider? specProvider, ILogger? logger) : base(blockhashProvider, specProvider, logger)
    {
        _traceTransfers = traceTransfers;
    }

    protected override void TraceAction<TTracingActions>(EvmState currentState)
    {
        if (_traceTransfers)
        {
            //Log action
            byte[]? data = AbiEncoder.Instance.Encode(AbiEncodingStyle.Packed,
                new AbiSignature("Transfer", AbiType.Address, AbiType.Address, AbiType.UInt256), currentState.From,
                currentState.To, currentState.Env.Value);
            LogEntry? result = new(Address.Zero, data, new[] { Keccak.Zero });
            currentState.Logs.Add(result);
        }

        //Do default stuff
        base.TraceAction<TTracingActions>(currentState);
    }
}
