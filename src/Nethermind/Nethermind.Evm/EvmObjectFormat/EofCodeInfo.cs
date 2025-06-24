// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Evm.EvmObjectFormat;

namespace Nethermind.Evm.CodeAnalysis;

public sealed class EofCodeInfo(in ValueHash256 codeHash, in EofContainer container) : ICodeInfo
{
    private readonly ValueHash256 _codeHash = codeHash;
    public ref readonly ValueHash256 CodeHash => ref _codeHash;
    public EofContainer EofContainer { get; private set; } = container;
    public ReadOnlyMemory<byte> MachineCode => EofContainer.Container;
    public int Version => EofContainer.Header.Version;
    public bool IsEmpty => EofContainer.IsEmpty;
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
