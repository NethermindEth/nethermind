// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

public partial class VirtualMachine<TGasPolicy>
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    protected readonly ref struct CallResult
    {
        public static CallResult Empty() => new(output: default, precompileSuccess: null);

        public CallResult(VmState<TGasPolicy> stateToExecute)
        {
            StateToExecute = stateToExecute;
            Output = Array.Empty<byte>();
            PooledOutput = null;
            PrecompileSuccess = null;
            ShouldRevert = false;
            ExceptionType = EvmExceptionType.None;
        }

        public CallResult(ReadOnlyMemory<byte> output, bool? precompileSuccess, bool shouldRevert = false, EvmExceptionType exceptionType = EvmExceptionType.None, byte[]? pooledOutput = null)
        {
            StateToExecute = null;
            Output = output;
            PooledOutput = pooledOutput;
            PrecompileSuccess = precompileSuccess;
            ShouldRevert = shouldRevert;
            ExceptionType = exceptionType;
        }

        public CallResult(EvmExceptionType exceptionType)
        {
            StateToExecute = null;
            Output = StatusCode.FailureBytes;
            PooledOutput = null;
            PrecompileSuccess = null;
            ShouldRevert = false;
            ExceptionType = exceptionType;
        }

        public VmState<TGasPolicy>? StateToExecute { get; }
        public ReadOnlyMemory<byte> Output { get; }
        /// <summary>The rented array behind <see cref="Output"/>, handed to the parent frame to recycle; null for a plain array.</summary>
        public byte[]? PooledOutput { get; }
        public EvmExceptionType ExceptionType { get; }
        public bool ShouldRevert { get; }
        public bool? PrecompileSuccess { get; }
        public bool IsReturn => StateToExecute is null;
        //EvmExceptionType.Revert is returned when the top frame encounters a REVERT opcode, which is not an exception.
        public bool IsException => ExceptionType != EvmExceptionType.None && ExceptionType != EvmExceptionType.Revert;
        public string? SubstateError { get; init; }
    }
}
