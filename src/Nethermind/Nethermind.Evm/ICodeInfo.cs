// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Evm.EOF;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis;

public interface ICodeInfo
{
    int Version => 0;
    ReadOnlyMemory<byte> MachineCode { get; }
    IPrecompile? Precompile { get; }
    bool IsPrecompile => Precompile is not null;
    ReadOnlyMemory<byte> TypeSection => Memory<byte>.Empty;
    ReadOnlyMemory<byte> CodeSection(int _) => MachineCode;
    ReadOnlyMemory<byte> DataSection => Memory<byte>.Empty;
    ReadOnlyMemory<byte> ContainerSection(int _) => Memory<byte>.Empty;
    SectionHeader SectionOffset(int idx);
    SectionHeader? ContainerOffset(int idx);
    (byte inputCount, byte outputCount, ushort maxStackHeight) GetSectionMetadata(int index) => (0, 0, 1024);
    bool ValidateJump(int destination, bool isSubroutine);
}
