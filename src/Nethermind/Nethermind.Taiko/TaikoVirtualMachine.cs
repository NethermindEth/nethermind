// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Logging;
using Nethermind.Taiko.Precompiles;

namespace Nethermind.Taiko;

/// <summary>
/// Taiko-specific <see cref="VirtualMachine{TGasPolicy}"/> that extends precompile dispatch with
/// support for <see cref="IPrecompileGasAware"/>. Gas-aware precompiles (currently only L1STATICCALL)
/// compute their actual gas consumption during <c>Run</c> — this is deducted in addition to the
/// standard base + data gas costs already handled by the base class.
/// </summary>
public class TaikoVirtualMachine<TGasPolicy>(
    IBlockhashProvider? blockHashProvider,
    ISpecProvider? specProvider,
    ILogManager? logManager
) : VirtualMachine<TGasPolicy>(blockHashProvider, specProvider, logManager)
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
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
                Result<(byte[] returnValue, long gasConsumed)> output = gasAwarePrecompile.Run(callData, spec, remainingGas);

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

        return base.ExecutePrecompileCall(state, precompile, callData, spec);
    }
}

public sealed class TaikoEthereumVirtualMachine(
    IBlockhashProvider? blockHashProvider,
    ISpecProvider? specProvider,
    ILogManager? logManager
) : TaikoVirtualMachine<EthereumGasPolicy>(blockHashProvider, specProvider, logManager), IVirtualMachine
{
}
