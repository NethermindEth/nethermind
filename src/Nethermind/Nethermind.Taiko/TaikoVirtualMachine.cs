// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Taiko.Precompiles;

namespace Nethermind.Taiko;

/// <summary>
/// Taiko-specific <see cref="VirtualMachine{TGasPolicy}"/> that extends precompile dispatch with a
/// single <see cref="IContextAwarePrecompile"/> branch. Context extras (remaining gas, L1 origin)
/// are bundled in <see cref="PrecompileExtras"/>; the L1 origin is read once per block from
/// <see cref="IL1OriginStore"/> in <see cref="SetBlockExecutionContext"/> and reused for every
/// precompile call in that block. <c>null</c> origin signals "no origin available" (preconf blocks
/// where <c>L1BlockHeight</c> is 0/null, or <c>eth_call</c> at a block with no stored origin) and
/// the precompile must treat that as permissive.
/// </summary>
public class TaikoVirtualMachine(
    IBlockhashProvider? blockHashProvider,
    ISpecProvider? specProvider,
    IL1OriginStore l1OriginStore,
    ILogManager? logManager
) : VirtualMachine<EthereumGasPolicy>(blockHashProvider, specProvider, logManager)
{
    private readonly IL1OriginStore _l1OriginStore = l1OriginStore ?? throw new ArgumentNullException(nameof(l1OriginStore));
    private UInt256? _blockL1Origin;

    public override void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        base.SetBlockExecutionContext(in blockExecutionContext);
        // Cache once per block — every precompile call in this block reuses the same value
        // instead of hitting the store. null when origin is missing (preconf / eth_call / pre-genesis).
        _blockL1Origin = _l1OriginStore.ReadL1Origin((UInt256)blockExecutionContext.Number)?.L1BlockHeight is long h && h > 0
            ? (UInt256)h
            : null;
    }

    protected override CallResult ExecutePrecompileCall(
        VmState<EthereumGasPolicy> state,
        IPrecompile precompile,
        ReadOnlyMemory<byte> callData,
        IReleaseSpec spec)
    {
        if (precompile is IContextAwarePrecompile contextAwarePrecompile)
        {
            EthereumGasPolicy gas = state.Gas;
            PrecompileExtras extras = new(
                remainingGas: EthereumGasPolicy.GetRemainingGas(in gas),
                l1Origin: _blockL1Origin);

            Result<(byte[] returnValue, ulong gasConsumed)> output;
            try
            {
                output = contextAwarePrecompile.Run(callData, spec, in extras);
            }
            catch (Exception exception) when (exception is DllNotFoundException or { InnerException: DllNotFoundException })
            {
                if (_logger.IsError) LogMissingDependency(precompile, (exception as DllNotFoundException ?? exception.InnerException as DllNotFoundException)!);
                Environment.Exit(ExitCodes.MissingPrecompile);
                throw; // Unreachable
            }
            catch (Exception exception)
            {
                if (_logger.IsError) LogExecutionException(precompile, exception);
                return new(default, precompileSuccess: false, shouldRevert: true);
            }

            // Deduct dynamic gas (e.g. actual L1 consumption) regardless of success/failure.
            // On L1 OOG the user loses the full gas limit — matching standard EVM sub-call semantics.
            ulong gasConsumed = output.Data.gasConsumed;
            if (gasConsumed > 0 && !EthereumGasPolicy.UpdateGas(ref gas, gasConsumed))
            {
                return new(default, precompileSuccess: false, shouldRevert: true, EvmExceptionType.OutOfGas);
            }

            state.Gas = gas;

            if (!output)
            {
                return new(
                    output.Data.returnValue ?? [],
                    precompileSuccess: false,
                    shouldRevert: true,
                    exceptionType: EvmExceptionType.PrecompileFailure
                )
                {
                    SubstateError = GetErrorString(precompile, output.Error)
                };
            }

            return new(
                output.Data.returnValue,
                precompileSuccess: true,
                shouldRevert: false,
                exceptionType: EvmExceptionType.None
            );
        }

        return base.ExecutePrecompileCall(state, precompile, callData, spec);
    }
}

public sealed class TaikoEthereumVirtualMachine(
    IBlockhashProvider? blockHashProvider,
    ISpecProvider? specProvider,
    IL1OriginStore l1OriginStore,
    ILogManager? logManager
) : TaikoVirtualMachine(blockHashProvider, specProvider, l1OriginStore, logManager), IVirtualMachine<EthereumGasPolicy>
{
}
