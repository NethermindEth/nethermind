// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Evm.EOF;
using Nethermind.Evm.Precompiles;
using static System.Collections.Specialized.BitVector32;

namespace Nethermind.Evm.CodeAnalysis;

public class EofCodeInfo : ICodeInfo
{
    private readonly ICodeInfo _codeInfo;

    private readonly EofHeader _header;
    public ReadOnlyMemory<byte> MachineCode => _codeInfo.MachineCode;
    public IPrecompile? Precompile => _codeInfo.Precompile;
    public int Version => _header.Version;
    public bool IsEmpty => _codeInfo.IsEmpty;
    public ReadOnlyMemory<byte> TypeSection { get; }
    public ReadOnlyMemory<byte> CodeSection { get; }
    public ReadOnlyMemory<byte> DataSection { get; }

    public ReadOnlyMemory<byte> ContainerSection(int index)
    {
        var offset = ContainerSectionOffset(index);
        if (offset  is null)
            return Memory<byte>.Empty;
        else
        {
            return MachineCode.Slice(_header.ContainerSection.Value.Start + offset.Value.Start, offset.Value.Size);
        }
    }
    public SectionHeader CodeSectionOffset(int sectionId) => _header.CodeSections[sectionId];
    public SectionHeader? ContainerSectionOffset(int containerId) =>
        _header.ContainerSection is null
            ? null
            : _header.ContainerSection.Value[containerId];
    public (byte inputCount, byte outputCount, ushort maxStackHeight) GetSectionMetadata(int index)
    {
        ReadOnlySpan<byte> typesectionSpan = TypeSection.Span;
        int TypeSectionSectionOffset = index * EvmObjectFormat.Eof1.MINIMUM_TYPESECTION_SIZE;
        return
            (
                typesectionSpan[TypeSectionSectionOffset + EvmObjectFormat.Eof1.INPUTS_OFFSET],
                typesectionSpan[TypeSectionSectionOffset + EvmObjectFormat.Eof1.OUTPUTS_OFFSET],
                typesectionSpan.Slice(TypeSectionSectionOffset + EvmObjectFormat.Eof1.MAX_STACK_HEIGHT_OFFSET, EvmObjectFormat.Eof1.MAX_STACK_HEIGHT_LENGTH).ReadEthUInt16()
            );
    }

    public EofCodeInfo(ICodeInfo codeInfo, in EofHeader header)
    {
        _codeInfo = codeInfo;
        _header = header;
        TypeSection = MachineCode.Slice(_header.TypeSection.Start, _header.TypeSection.Size);
        DataSection = MachineCode.Slice(_header.DataSection.Start, _header.DataSection.Size);
        CodeSection = MachineCode.Slice(_header.CodeSections.Start, _header.CodeSections.Size);
    }
}
