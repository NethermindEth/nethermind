// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

public sealed unsafe partial class VirtualMachine
{
    internal readonly ref struct CallResult
    {
        public static CallResult InvalidSubroutineEntry => new(EvmExceptionType.InvalidSubroutineEntry);
        public static CallResult InvalidSubroutineReturn => new(EvmExceptionType.InvalidSubroutineReturn);
        public static CallResult OutOfGasException => new(EvmExceptionType.OutOfGas);
        public static CallResult AccessViolationException => new(EvmExceptionType.AccessViolation);
        public static CallResult InvalidJumpDestination => new(EvmExceptionType.InvalidJumpDestination);
        public static CallResult InvalidInstructionException => new(EvmExceptionType.BadInstruction);
        public static CallResult StaticCallViolationException => new(EvmExceptionType.StaticCallViolation);
        public static CallResult StackOverflowException => new(EvmExceptionType.StackOverflow);
        public static CallResult StackUnderflowException => new(EvmExceptionType.StackUnderflow);
        public static CallResult InvalidCodeException => new(EvmExceptionType.InvalidCode);
        public static CallResult InvalidAddressRange => new(EvmExceptionType.AddressOutOfRange);
        public static CallResult Empty(int fromVersion) => new(container: null, output: default, precompileSuccess: null, fromVersion);

        public CallResult(EvmState stateToExecute)
        {
            StateToExecute = stateToExecute;
            Output = (null, Array.Empty<byte>());
            PrecompileSuccess = null;
            ShouldRevert = false;
            ExceptionType = EvmExceptionType.None;
        }

        public CallResult(ReadOnlyMemory<byte> output, bool? precompileSuccess, int fromVersion, bool shouldRevert = false, EvmExceptionType exceptionType = EvmExceptionType.None)
        {
            StateToExecute = null;
            Output = (null, output);
            PrecompileSuccess = precompileSuccess;
            ShouldRevert = shouldRevert;
            ExceptionType = exceptionType;
            FromVersion = fromVersion;
        }

        public CallResult(ICodeInfo? container, ReadOnlyMemory<byte> output, bool? precompileSuccess, int fromVersion, bool shouldRevert = false, EvmExceptionType exceptionType = EvmExceptionType.None)
        {
            StateToExecute = null;
            Output = (container, output);
            PrecompileSuccess = precompileSuccess;
            ShouldRevert = shouldRevert;
            ExceptionType = exceptionType;
            FromVersion = fromVersion;
        }

        private CallResult(EvmExceptionType exceptionType)
        {
            StateToExecute = null;
            Output = (null, StatusCode.FailureBytes);
            PrecompileSuccess = null;
            ShouldRevert = false;
            ExceptionType = exceptionType;
        }

        public EvmState? StateToExecute { get; }
        public (ICodeInfo Container, ReadOnlyMemory<byte> Bytes) Output { get; }
        public EvmExceptionType ExceptionType { get; }
        public bool ShouldRevert { get; }
        public bool? PrecompileSuccess { get; }
        public bool IsReturn => StateToExecute is null;
        public bool IsException => ExceptionType != EvmExceptionType.None;
        public int FromVersion { get; }
    }
}
