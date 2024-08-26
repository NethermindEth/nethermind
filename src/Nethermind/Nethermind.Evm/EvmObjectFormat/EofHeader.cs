// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.EvmObjectFormat.Handlers;
using System;
using System.Linq;

namespace Nethermind.Evm.EvmObjectFormat;


public struct EofContainer
{
    public ReadOnlyMemory<byte> Container;
    public bool IsEmpty => Container.IsEmpty;

    public EofContainer(ReadOnlyMemory<byte> container, EofHeader eofHeader)
    {
        Container = container;
        Header = eofHeader;
        Prefix = container.Slice(0, eofHeader.PrefixSize);
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

        DataSection = container.Slice(eofHeader.DataSection.Start);
    }

    public EofHeader Header;
    public ReadOnlyMemory<byte> Prefix;

    public ReadOnlyMemory<byte> TypeSection;
    public ReadOnlyMemory<byte>[] TypeSections;

    public ReadOnlyMemory<byte> CodeSection;
    public ReadOnlyMemory<byte>[] CodeSections;


    public ReadOnlyMemory<byte> ContainerSection;
    public ReadOnlyMemory<byte>[] ContainerSections;
    public ReadOnlyMemory<byte> DataSection;
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

public struct SectionHeader(int start, ushort size)
{
    public readonly int Start => start;
    public readonly int Size => size;
    public readonly int EndOffset => Start + Size;

    public static implicit operator Range(SectionHeader section) => new(section.Start, section.EndOffset);
}

public struct CompoundSectionHeader(int start, int[] subSectionsSizes)
{
    public readonly int Start => start;

    public readonly int[] SubSectionsSizes = subSectionsSizes;

    public readonly int EndOffset => Start + SubSectionsSizes.Sum();
    public readonly int Size => EndOffset - Start;
    public readonly int Count => SubSectionsSizes.Length;

    private int[] subSectionsSizesAcc;
    private int[] SubSectionsSizesAcc
    {
        get
        {
            if (subSectionsSizesAcc is null)
            {
                subSectionsSizesAcc = new int[SubSectionsSizes.Length];
                subSectionsSizesAcc[0] = 0;
                for (var i = 1; i < SubSectionsSizes.Length; i++)
                {
                    subSectionsSizesAcc[i] = subSectionsSizesAcc[i - 1] + SubSectionsSizes[i - 1];
                }
            }

            return subSectionsSizesAcc;
        }
    }

    public SectionHeader this[int i] => new SectionHeader(SubSectionsSizesAcc[i], (ushort)SubSectionsSizes[i]);

    public static implicit operator Range(CompoundSectionHeader section) => new(section.Start, section.EndOffset);
}

