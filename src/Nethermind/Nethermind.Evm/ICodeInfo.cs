// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// Represents common code information for EVM execution. 
/// Implementations include <see cref="CodeInfo"/>, <see cref="EofCodeInfo"/> (EVM Object Format), 
/// and <see cref="PrecompileInfo"/> for precompiled contracts.
/// </summary>
public interface ICodeInfo
{
    /// <summary>
    /// Gets the version of the code format. 
    /// The default implementation returns 0, representing a legacy code format or non-EOF code.
    /// </summary>
    int Version => 0;

    /// <summary>
    /// Indicates whether the code is empty or not.
    /// </summary>
    bool IsEmpty { get; }

    /// <summary>
    /// Gets the raw machine code as a <see cref="ReadOnlyMemory{Byte}"/> segment.
    /// This is the primary code section from which the EVM executes instructions.
    /// </summary>
    ReadOnlyMemory<byte> MachineCode { get; }

    /// <summary>
    /// Indicates whether this code represents a precompiled contract.
    /// By default, this returns <c>false</c>.
    /// </summary>
    bool IsPrecompile => false;

    /// <summary>
    /// Gets the code section. 
    /// By default, this returns the same contents as <see cref="MachineCode"/>.
    /// </summary>
    ReadOnlyMemory<byte> CodeSection => MachineCode;

    /// <summary>
    /// Gets the data section, which is reserved for additional data segments in EOF.
    /// By default, this returns an empty memory segment.
    /// </summary>
    ReadOnlyMemory<byte> DataSection => Memory<byte>.Empty;

    /// <summary>
    /// Computes the offset to be added to the program counter when executing instructions.
    /// By default, this returns 0, meaning no offset is applied.
    /// </summary>
    /// <returns>The program counter offset for this code format.</returns>
    int PcOffset() => 0;

    /// <summary>
    /// Validates whether a jump destination is permissible according to this code format.
    /// By default, this returns <c>false</c>.
    /// </summary>
    /// <param name="destination">The instruction index to validate.</param>
    /// <returns><c>true</c> if the jump is valid; otherwise, <c>false</c>.</returns>
    bool ValidateJump(int destination) => false;
}
