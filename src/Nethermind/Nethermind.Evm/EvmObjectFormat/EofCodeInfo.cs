// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Evm.EOF;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis;

public class EofCodeInfo : ICodeInfo
{
    private readonly ICodeInfo _codeInfo;

    public EofHeader Header { get; private set; }
    public ReadOnlyMemory<byte> MachineCode => _codeInfo.MachineCode;
    public IPrecompile? Precompile => _codeInfo.Precompile;
    public int Version => Header.Version;
    public bool IsEmpty => _codeInfo.IsEmpty;
    public ReadOnlyMemory<byte> TypeSection { get; }
    public ReadOnlyMemory<byte> CodeSection { get; }
    public ReadOnlyMemory<byte> DataSection { get; }
    public ReadOnlyMemory<byte> ContainerSection { get; }

    public SectionHeader CodeSectionOffset(int sectionId) => Header.CodeSections[sectionId];
    public SectionHeader? ContainerSectionOffset(int containerId) =>
        Header.ContainerSections is null
            ? null
            : Header.ContainerSections.Value[containerId];
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
        Header = header;
        TypeSection = MachineCode.Slice(Header.TypeSection.Start, Header.TypeSection.Size);
        CodeSection = MachineCode.Slice(Header.CodeSections.Start, Header.CodeSections.Size);
        DataSection = MachineCode.Slice(Header.DataSection.Start);
        ContainerSection = Header.ContainerSections is null ? Memory<byte>.Empty : MachineCode.Slice(Header.ContainerSections.Value.Start, Header.ContainerSections.Value.Size);
    }
}
