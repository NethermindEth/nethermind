// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.EOF;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis;

public interface ICodeInfo
{
    int Version => 0;
    bool IsEmpty { get; }
    ReadOnlyMemory<byte> MachineCode { get; }
    IPrecompile? Precompile { get; }
    bool IsPrecompile => Precompile is not null;
    ReadOnlyMemory<byte> TypeSection => Memory<byte>.Empty;
    ReadOnlyMemory<byte> CodeSection => MachineCode;
    ReadOnlyMemory<byte> DataSection => Memory<byte>.Empty;
    ReadOnlyMemory<byte> ContainerSection => Memory<byte>.Empty;
    SectionHeader CodeSectionOffset(int idx);
    SectionHeader? ContainerSectionOffset(int idx);
    (byte inputCount, byte outputCount, ushort maxStackHeight) GetSectionMetadata(int index) => (0, 0, 1024);
    void AnalyseInBackgroundIfRequired() { }
}
