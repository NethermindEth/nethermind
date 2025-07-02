// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Evm.EvmObjectFormat.Handlers;

namespace Nethermind.Evm.EvmObjectFormat;

public readonly struct EofContainer
{
    public readonly ReadOnlyMemory<byte> Container;
    public bool IsEmpty => Container.IsEmpty;

    // https://eips.ethereum.org/EIPS/eip-3540#section-structure
    public EofContainer(ReadOnlyMemory<byte> container, EofHeader eofHeader)
    {
        Container = container;
        Header = eofHeader;
        Prefix = container[..eofHeader.PrefixSize];
        TypeSection = container[(Range)eofHeader.TypeSection];
        CodeSection = container[(Range)eofHeader.CodeSections];
        ContainerSection = eofHeader.ContainerSections.HasValue ? container[(Range)eofHeader.ContainerSections.Value] : ReadOnlyMemory<byte>.Empty;

        TypeSections = new ReadOnlyMemory<byte>[eofHeader.CodeSections.Count];
        for (var i = 0; i < eofHeader.CodeSections.Count; i++)
        {
            TypeSections[i] = TypeSection.Slice(i * Eof1.MINIMUM_TYPESECTION_SIZE, Eof1.MINIMUM_TYPESECTION_SIZE);
        }

        CodeSections = new ReadOnlyMemory<byte>[eofHeader.CodeSections.Count];
        for (var i = 0; i < eofHeader.CodeSections.Count; i++)
        {
            CodeSections[i] = CodeSection[(Range)Header.CodeSections[i]];
        }

        if (eofHeader.ContainerSections.HasValue)
        {
            ContainerSections = new ReadOnlyMemory<byte>[eofHeader.ContainerSections.Value.Count];
            for (var i = 0; i < eofHeader.ContainerSections.Value.Count; i++)
            {
                ContainerSections[i] = ContainerSection[(Range)Header.ContainerSections.Value[i]];
            }
        }
        else
        {
            ContainerSections = Array.Empty<ReadOnlyMemory<byte>>();
        }

        DataSection = container[eofHeader.DataSection.Start..];
    }

    public readonly EofHeader Header;
    public readonly ReadOnlyMemory<byte> Prefix;

    public readonly ReadOnlyMemory<byte> TypeSection;
    public readonly ReadOnlyMemory<byte>[] TypeSections;

    public readonly ReadOnlyMemory<byte> CodeSection;
    public readonly ReadOnlyMemory<byte>[] CodeSections;

    public readonly ReadOnlyMemory<byte> ContainerSection;
    public readonly ReadOnlyMemory<byte>[] ContainerSections;
    public readonly ReadOnlyMemory<byte> DataSection;

    public (byte inputCount, byte outputCount, ushort maxStackHeight) GetSectionMetadata(int index)
    {
        ReadOnlySpan<byte> typeSection = TypeSections[index].Span;
        return
            (
                typeSection[Eof1.INPUTS_OFFSET],
                typeSection[Eof1.OUTPUTS_OFFSET],
                typeSection.Slice(Eof1.MAX_STACK_HEIGHT_OFFSET, Eof1.MAX_STACK_HEIGHT_LENGTH).ReadEthUInt16()
            );
    }
}

public struct EofHeader()
{
    public required byte Version;
    public required int PrefixSize;
    public required SectionHeader TypeSection;
    public required CompoundSectionHeader CodeSections;
    public required CompoundSectionHeader? ContainerSections;
    public required SectionHeader DataSection;
}

public readonly struct SectionHeader(int start, ushort size)
{
    public readonly int Start => start;
    public readonly int Size => size;
    public readonly int EndOffset => Start + Size;

    public static implicit operator Range(SectionHeader section) => new(section.Start, section.EndOffset);
}

public readonly struct CompoundSectionHeader(int start, int[] subSectionsSizes)
{
    public readonly int Start => start;

    public readonly int[] SubSectionsSizes = subSectionsSizes;

    public readonly int EndOffset => Start + SubSectionsSizes.Sum();
    public readonly int Size => EndOffset - Start;
    public readonly int Count => SubSectionsSizes.Length;

    private static int[] CreateSubSectionsSizes(int[] subSectionsSizes)
    {
        var subSectionsSizesAcc = new int[subSectionsSizes.Length];
        subSectionsSizesAcc[0] = 0;
        for (var i = 1; i < subSectionsSizes.Length; i++)
        {
            subSectionsSizesAcc[i] = subSectionsSizesAcc[i - 1] + subSectionsSizes[i - 1];
        }

        return subSectionsSizesAcc;
    }

    private int[] SubSectionsSizesAcc { get; } = CreateSubSectionsSizes(subSectionsSizes);

    public SectionHeader this[int i] => new(SubSectionsSizesAcc[i], (ushort)SubSectionsSizes[i]);

    public static implicit operator Range(CompoundSectionHeader section) => new(section.Start, section.EndOffset);
}

