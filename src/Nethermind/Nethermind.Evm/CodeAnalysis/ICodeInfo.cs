// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
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
    (int start, int size) SectionOffset(int idx) => idx == 0 ? (0, MachineCode.Length) : throw new ArgumentOutOfRangeException();
    (int start, int size) ContainerOffset(int idx) => idx == 0 ? (0, 0) : throw new ArgumentOutOfRangeException();
    (byte inputCount, byte outputCount, ushort maxStackHeight) GetSectionMetadata(int index) => (0, 0, 1024);
    bool ValidateJump(int destination, bool isSubroutine);
}
