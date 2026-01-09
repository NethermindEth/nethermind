// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Evm
{
    public abstract class EvmException : Exception
    {
        public abstract EvmExceptionType ExceptionType { get; }
    }

    public enum EvmExceptionType : int
    {
        Stop = -1,
        None = 0,
        Return,
        Revert,
        BadInstruction,
        StackOverflow,
        StackUnderflow,
        OutOfGas,
        GasUInt64Overflow,
        InvalidSubroutineEntry,
        InvalidSubroutineReturn,
        InvalidJumpDestination,
        AccessViolation,
        AddressOutOfRange,
        StaticCallViolation,
        PrecompileFailure,
        TransactionCollision,
        NotEnoughBalance,
        Other,
        InvalidCode,
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct OpcodeResult
    {
        public readonly ulong _data;
        public readonly int ProgramCounter
        {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return (int)(uint)_data;
            }
        }

        public readonly EvmExceptionType Exception
        {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return (EvmExceptionType)(_data >> 32);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public OpcodeResult(int pc, EvmExceptionType ex)
        {
            _data = ((ulong)(uint)ex << 32) | (uint)pc;
        }
    }
}
