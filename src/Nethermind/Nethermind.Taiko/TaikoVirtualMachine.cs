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
/// Taiko-specific <see cref="VirtualMachine{TGasPolicy}"/> that extends precompile dispatch with
/// <see cref="IPrecompileGasAware"/> and <see cref="IL1OriginAware"/> support. The L1 origin
/// (read from <see cref="IL1OriginStore"/> keyed by the current L2 block number) is passed to
/// origin-aware precompiles as a method argument; <c>null</c> signals "no origin available"
/// (preconf blocks where <c>L1BlockHeight</c> is 0/null, or <c>eth_call</c> at a block with no
/// stored origin) and the precompile must treat that as permissive.
/// </summary>
public class TaikoVirtualMachine<TGasPolicy>(
    IBlockhashProvider? blockHashProvider,
    ISpecProvider? specProvider,
    IL1OriginStore l1OriginStore,
    ILogManager? logManager
) : VirtualMachine<TGasPolicy>(blockHashProvider, specProvider, logManager)
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    private readonly IL1OriginStore _l1OriginStore = l1OriginStore ?? throw new ArgumentNullException(nameof(l1OriginStore));

    private UInt256? GetL1Origin() =>
        _l1OriginStore.ReadL1Origin((UInt256)BlockExecutionContext.Number)?.L1BlockHeight is long h && h > 0
            ? (UInt256)h
            : null;

    protected override CallResult ExecutePrecompileCall(
        VmState<TGasPolicy> state,
        IPrecompile precompile,
        ReadOnlyMemory<byte> callData,
        IReleaseSpec spec)
    {
        if (precompile is IPrecompileGasAware gasAwarePrecompile)
        {
            TGasPolicy gas = state.Gas;
            long remainingGas = TGasPolicy.GetRemainingGas(in gas);

            try
            {
                Result<(byte[] returnValue, long gasConsumed)> output = precompile is IPrecompileGasAndOriginAware bothAware
                    ? bothAware.Run(callData, spec, remainingGas, GetL1Origin())
                    : gasAwarePrecompile.Run(callData, spec, remainingGas);

                // Deduct dynamic gas (actual L1 consumption) regardless of success/failure.
                // On L1 OOG the user loses the full gas limit — matching standard EVM sub-call semantics.
                long gasConsumed = output.Data.gasConsumed;
                if (gasConsumed > 0 && !TGasPolicy.UpdateGas(ref gas, gasConsumed))
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
        }

        if (precompile is IL1OriginAware originAwarePrecompile)
        {
            try
            {
                Result<byte[]> output = originAwarePrecompile.Run(callData, spec, GetL1Origin());
                bool success = output;
                return new(
                    success ? output.Data : [],
                    precompileSuccess: success,
                    shouldRevert: !success,
                    exceptionType: !success ? EvmExceptionType.PrecompileFailure : EvmExceptionType.None
                )
                {
                    SubstateError = success ? null : GetErrorString(precompile, output.Error)
                };
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
        }

        return base.ExecutePrecompileCall(state, precompile, callData, spec);
    }
}

public sealed class TaikoEthereumVirtualMachine(
    IBlockhashProvider? blockHashProvider,
    ISpecProvider? specProvider,
    IL1OriginStore l1OriginStore,
    ILogManager? logManager
) : TaikoVirtualMachine<EthereumGasPolicy>(blockHashProvider, specProvider, l1OriginStore, logManager), IVirtualMachine
{
}
