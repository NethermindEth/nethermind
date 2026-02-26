// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

public partial class VirtualMachine<TGasPolicy>
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    [StructLayout(LayoutKind.Auto)]
    protected readonly ref struct CallResult
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
        public static CallResult Empty(int fromVersion)
        {
            CallResult result = default;
            if (fromVersion > 0)
            {
                Unsafe.AsRef(in result._fromVersion) = (byte)fromVersion;
            }

            return result;
        }

        public CallResult(VmState<TGasPolicy> stateToExecute)
        {
            StateToExecute = stateToExecute;
            DeployCode = null;
            _resultType = CallResultType.EvmCall;
            ShouldRevert = false;
            ExceptionType = EvmExceptionType.None;
        }

        /// <summary>
        /// Constructor for regular EVM call returns where output is stored in ReturnDataBuffer externally.
        /// </summary>
        public CallResult(ICodeInfo? container, int fromVersion, bool shouldRevert = false, EvmExceptionType exceptionType = EvmExceptionType.None)
        {
            StateToExecute = null;
            DeployCode = container;
            _resultType = CallResultType.EvmCall;
            ShouldRevert = shouldRevert;
            ExceptionType = exceptionType;
            _fromVersion = (byte)fromVersion;
        }

        /// <summary>
        /// Constructor for precompile results where output is stored in ReturnDataBuffer externally.
        /// </summary>
        public CallResult(bool precompileSuccess, bool shouldRevert = false, EvmExceptionType exceptionType = EvmExceptionType.None)
        {
            StateToExecute = null;
            DeployCode = null;
            _resultType = precompileSuccess ? CallResultType.PrecompileSuccess : CallResultType.PrecompileFailure;
            ShouldRevert = shouldRevert;
            ExceptionType = exceptionType;
            _fromVersion = 0;
        }

        private CallResult(EvmExceptionType exceptionType)
        {
            StateToExecute = null;
            DeployCode = null;
            _resultType = CallResultType.EvmCall;
            ShouldRevert = false;
            ExceptionType = exceptionType;
        }

        private readonly CallResultType _resultType;

        public VmState<TGasPolicy>? StateToExecute { get; }
        public ICodeInfo? DeployCode { get; }
        public EvmExceptionType ExceptionType { get; }
        public bool ShouldRevert { get; }
        public bool? PrecompileSuccess => _resultType switch
        {
            CallResultType.PrecompileSuccess => true,
            CallResultType.PrecompileFailure => false,
            _ => null
        };
        public bool IsReturn => StateToExecute is null;
        //EvmExceptionType.Revert is returned when the top frame encounters a REVERT opcode, which is not an exception.
        public bool IsException => ExceptionType != EvmExceptionType.None && ExceptionType != EvmExceptionType.Revert;
        private readonly byte _fromVersion;
        public int FromVersion => (int)(uint)_fromVersion;
        public string? SubstateError { get; init; }
        /// <summary>
        /// Indicates the result type of a call execution.
        /// </summary>
        private enum CallResultType : byte
        {
            /// <summary>Regular EVM bytecode execution (not a precompile).</summary>
            EvmCall = 0,
            /// <summary>Precompile executed successfully.</summary>
            PrecompileSuccess = 1,
            /// <summary>Precompile execution failed.</summary>
            PrecompileFailure = 2
        }
    }
}
