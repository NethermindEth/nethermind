// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Evm.EOF;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis;

public class EofCodeInfo : ICodeInfo
{
    private readonly CodeInfo _codeInfo;

    private readonly EofHeader _header;
    public ReadOnlyMemory<byte> MachineCode => _codeInfo.MachineCode;
    public IPrecompile? Precompile => _codeInfo.Precompile;
    public int Version => _header.Version;
    public ReadOnlyMemory<byte> TypeSection { get; }
    public ReadOnlyMemory<byte> CodeSection { get; }
    public ReadOnlyMemory<byte> DataSection { get; }
    public ReadOnlyMemory<byte>? ContainerSection { get; }
    public SectionHeader SectionOffset(int sectionId) => _header.CodeSections[sectionId];
    public SectionHeader ContainerOffset(int containerId) =>
        _header.ContainerSection is null
            ? throw new System.Diagnostics.UnreachableException()
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

    public bool ValidateJump(int destination, bool isSubroutine)
    {
        throw new NotImplementedException();
    }

    public EofCodeInfo(CodeInfo codeInfo, in EofHeader header)
    {
        _codeInfo = codeInfo;
        _header = header;
        TypeSection = MachineCode.Slice(_header.TypeSection.Start, _header.TypeSection.Size);
        CodeSection = MachineCode.Slice(_header.CodeSections.Start, _header.CodeSections.Size);
        DataSection = MachineCode.Slice(_header.DataSection.Start, _header.DataSection.Size);
        ContainerSection = _header.ContainerSection is null ? null
            : MachineCode.Slice(_header.ContainerSection.Value.Start, _header.ContainerSection.Value.Size);
    }
}
