// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Evm.EvmObjectFormat.Handlers;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis;

public class EofCodeInfo : ICodeInfo
{
    public EofContainer EofContainer { get; private set; }
    public ReadOnlyMemory<byte> MachineCode => EofContainer.Container;
    public IPrecompile? Precompile => null;
    public int Version => EofContainer.Header.Version;
    public bool IsEmpty => EofContainer.IsEmpty;
    public ReadOnlyMemory<byte> TypeSection => EofContainer.TypeSection;
    public ReadOnlyMemory<byte> CodeSection => EofContainer.CodeSection;
    public ReadOnlyMemory<byte> DataSection => EofContainer.DataSection;
    public ReadOnlyMemory<byte> ContainerSection => EofContainer.ContainerSection;

    public SectionHeader CodeSectionOffset(int sectionId) => EofContainer.Header.CodeSections[sectionId];
    public SectionHeader? ContainerSectionOffset(int sectionId) => EofContainer.Header.ContainerSections.Value[sectionId];
    public (byte inputCount, byte outputCount, ushort maxStackHeight) GetSectionMetadata(int index)
    {
        ReadOnlySpan<byte> typesectionSpan = EofContainer.TypeSections[index].Span;
        return
            (
                typesectionSpan[Eof1.INPUTS_OFFSET],
                typesectionSpan[Eof1.OUTPUTS_OFFSET],
                typesectionSpan.Slice(Eof1.MAX_STACK_HEIGHT_OFFSET, Eof1.MAX_STACK_HEIGHT_LENGTH).ReadEthUInt16()
            );
    }

    public EofCodeInfo(in EofContainer container)
    {
        EofContainer = container;
    }
}
