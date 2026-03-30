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
            PrecompileSuccess = null;
            ShouldRevert = false;
            ExceptionType = EvmExceptionType.None;
        }

        public CallResult(ReadOnlyMemory<byte> output, bool? precompileSuccess, bool shouldRevert = false, EvmExceptionType exceptionType = EvmExceptionType.None)
        {
            StateToExecute = null;
            Output = output;
            PrecompileSuccess = precompileSuccess;
            ShouldRevert = shouldRevert;
            ExceptionType = exceptionType;
        }

        public CallResult(EvmExceptionType exceptionType)
        {
            StateToExecute = null;
            Output = StatusCode.FailureBytes;
            PrecompileSuccess = null;
            ShouldRevert = false;
            ExceptionType = exceptionType;
        }

        public VmState<TGasPolicy>? StateToExecute { get; }
        public ReadOnlyMemory<byte> Output { get; }
        public EvmExceptionType ExceptionType { get; }
        public bool ShouldRevert { get; }
        public bool? PrecompileSuccess { get; }
        public bool IsReturn => StateToExecute is null;
        //EvmExceptionType.Revert is returned when the top frame encounters a REVERT opcode, which is not an exception.
        public bool IsException => ExceptionType != EvmExceptionType.None && ExceptionType != EvmExceptionType.Revert;
        public string? SubstateError { get; init; }
    }
}
