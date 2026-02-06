// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.EvmObjectFormat;

namespace Nethermind.Evm.CodeAnalysis;

public sealed class EofCodeInfo : CodeInfo
{
    public EofCodeInfo(in EofContainer container) : base(container.Header.Version, container.Container)
    {
        EofContainer = container;
    }

    public EofContainer EofContainer { get; private set; }
    public ReadOnlyMemory<byte> TypeSection => EofContainer.TypeSection;
    public ReadOnlyMemory<byte> CodeSection => EofContainer.CodeSection;
    public ReadOnlyMemory<byte> DataSection => EofContainer.DataSection;
    public ReadOnlyMemory<byte> ContainerSection => EofContainer.ContainerSection;

    public SectionHeader CodeSectionOffset(int sectionId) => EofContainer.Header.CodeSections[sectionId];
    public SectionHeader? ContainerSectionOffset(int sectionId) => EofContainer.Header.ContainerSections.Value[sectionId];
    public int PcOffset() => EofContainer.Header.CodeSections.Start;

    public (byte inputCount, byte outputCount, ushort maxStackHeight) GetSectionMetadata(int index)
        => EofContainer.GetSectionMetadata(index);
}
