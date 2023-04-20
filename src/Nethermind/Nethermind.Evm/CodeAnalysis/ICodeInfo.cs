// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis;

public interface ICodeInfo
{
    byte[] MachineCode { get; }
    IPrecompile? Precompile { get; }
    bool IsPrecompile => Precompile is not null;
    ReadOnlyMemory<byte> TypeSection => Memory<byte>.Empty;
    ReadOnlyMemory<byte> CodeSection => MachineCode;
    ReadOnlyMemory<byte> DataSection => Memory<byte>.Empty;
    ReadOnlyMemory<byte> ContainerSection => Memory<byte>.Empty;
    (int, int) SectionOffset(int idx) => idx == 0 ? (0, MachineCode.Length) : throw new ArgumentOutOfRangeException();
    (int, int) ContainerOffset(int idx) => idx == 0 ? (0, 0) : throw new ArgumentOutOfRangeException();
    bool ValidateJump(int destination, bool isSubroutine);
}
