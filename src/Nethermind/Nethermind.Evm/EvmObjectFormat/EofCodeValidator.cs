// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DotNetty.Common.Utilities;
using FastEnumUtility;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

[assembly: InternalsVisibleTo("Nethermind.EofParser")]

namespace Nethermind.Evm.EOF;

public static class EvmObjectFormat
{
    [StructLayout(LayoutKind.Sequential)]
    struct Worklet
    {
        public Worklet(ushort position, StackBounds stackHeightBounds)
        {
            Position = position;
            StackHeightBounds = stackHeightBounds;
        }
        public ushort Position;
        public StackBounds StackHeightBounds;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StackBounds()
    {
        public short Max = -1;
        public short Min = 1023;

        public void Combine(StackBounds other)
        {
            this.Max = Math.Max(this.Max, other.Max);
            this.Min = Math.Min(this.Min, other.Min);
        }

        public bool BoundsEqual() => Max == Min;

        public static bool operator ==(StackBounds left, StackBounds right) => left.Max == right.Max && right.Min == left.Min;
        public static bool operator !=(StackBounds left, StackBounds right) => !(left == right);
        public override bool Equals(object obj) => obj is StackBounds && this == (StackBounds)obj;
        public override int GetHashCode() => Max ^ Min;
    }

    public enum ValidationStrategy
    {
        None = 0,
        Validate = 1,
        ValidateFullBody = Validate | 2,
        ValidateInitcodeMode = Validate | 4,
        ValidateRuntimeMode = Validate | 8,
        AllowTrailingBytes = Validate | 16,
        ExractHeader = 32,
        HasEofMagic = 64,

    }

    private interface IEofVersionHandler
    {
        bool ValidateBody(ReadOnlySpan<byte> code, EofHeader header, ValidationStrategy strategy);
        bool TryParseEofHeader(ReadOnlySpan<byte> code, [NotNullWhen(true)] out EofHeader? header);
    }

    // magic prefix : EofFormatByte is the first byte, EofFormatDiff is chosen to diff from previously rejected contract according to EIP3541
    public static byte[] MAGIC = { 0xEF, 0x00 };
    public const byte ONE_BYTE_LENGTH = 1;
    public const byte TWO_BYTE_LENGTH = 2;
    public const byte VERSION_OFFSET = TWO_BYTE_LENGTH; // magic lenght

    private static readonly Dictionary<byte, IEofVersionHandler> _eofVersionHandlers = new();
    internal static ILogger Logger { get; set; } = NullLogger.Instance;

    static EvmObjectFormat()
    {
        _eofVersionHandlers.Add(Eof1.VERSION, new Eof1());
    }

    /// <summary>
    /// returns whether the code passed is supposed to be treated as Eof regardless of its validity.
    /// </summary>
    /// <param name="container">Machine code to be checked</param>
    /// <returns></returns>
    public static bool IsEof(ReadOnlySpan<byte> container, [NotNullWhen(true)] out int version)
    {
        if (container.Length >= MAGIC.Length + 1)
        {
            version = container[MAGIC.Length];
            return container.StartsWith(MAGIC);
        }
        else
        {
            version = 0;
            return false;
        }

    }

    public static bool IsValidEof(ReadOnlySpan<byte> container, ValidationStrategy strategy, [NotNullWhen(true)] out EofHeader? header)
    {
        if (strategy == ValidationStrategy.None)
        {
            header = null;
            return true;
        }

        if (strategy.HasFlag(ValidationStrategy.HasEofMagic) && !container.StartsWith(MAGIC))
        {
            header = null;
            return false;
        }

        if (container.Length > VERSION_OFFSET
            && _eofVersionHandlers.TryGetValue(container[VERSION_OFFSET], out IEofVersionHandler handler)
            && handler.TryParseEofHeader(container, out header))
        {
            bool validateBody = strategy.HasFlag(ValidationStrategy.Validate);
            if (validateBody && handler.ValidateBody(container, header.Value, strategy))
            {
                return true;
            }
            return !validateBody;
        }

        header = null;
        return false;
    }

    internal class Eof1 : IEofVersionHandler
    {
        private ref struct Sizes
        {
            public ushort? TypeSectionSize;
            public ushort? CodeSectionSize;
            public ushort? DataSectionSize;
            public ushort? ContainerSectionSize;
        }

        public const byte VERSION = 0x01;
        internal enum Separator : byte
        {
            KIND_TYPE = 0x01,
            KIND_CODE = 0x02,
            KIND_CONTAINER = 0x03,
            KIND_DATA = 0x04,
            TERMINATOR = 0x00
        }

        internal const byte MINIMUM_HEADER_SECTION_SIZE = 3;
        internal const byte MINIMUM_TYPESECTION_SIZE = 4;
        internal const byte MINIMUM_CODESECTION_SIZE = 1;
        internal const byte MINIMUM_DATASECTION_SIZE = 0;
        internal const byte MINIMUM_CONTAINERSECTION_SIZE = 0;
        internal const byte MINIMUM_HEADER_SIZE = VERSION_OFFSET
                                                + MINIMUM_HEADER_SECTION_SIZE
                                                + MINIMUM_HEADER_SECTION_SIZE + TWO_BYTE_LENGTH
                                                + MINIMUM_HEADER_SECTION_SIZE
                                                + ONE_BYTE_LENGTH;

        internal const byte BYTE_BIT_COUNT = 8; // indicates the length of the count immediate of jumpv
        internal const byte MINIMUMS_ACCEPTABLE_JUMPV_JUMPTABLE_LENGTH = 1; // indicates the length of the count immediate of jumpv

        internal const byte INPUTS_OFFSET = 0;
        internal const byte INPUTS_MAX = 0x7F;

        internal const byte OUTPUTS_OFFSET = INPUTS_OFFSET + 1;
        internal const byte OUTPUTS_MAX = 0x7F;
        internal const byte NON_RETURNING = 0x80;

        internal const byte MAX_STACK_HEIGHT_OFFSET = OUTPUTS_OFFSET + 1;
        internal const int MAX_STACK_HEIGHT_LENGTH = 2;
        internal const ushort MAX_STACK_HEIGHT = 0x400;

        internal const ushort MINIMUM_NUM_CODE_SECTIONS = 1;
        internal const ushort MAXIMUM_NUM_CODE_SECTIONS = 1024;
        internal const ushort MAXIMUM_NUM_CONTAINER_SECTIONS = 0x00FF;
        internal const ushort RETURN_STACK_MAX_HEIGHT = MAXIMUM_NUM_CODE_SECTIONS; // the size in the type sectionn allocated to each function section

        internal const ushort MINIMUM_SIZE = MINIMUM_HEADER_SIZE
                                            + MINIMUM_TYPESECTION_SIZE // minimum type section body size
                                            + MINIMUM_CODESECTION_SIZE // minimum code section body size
                                            + MINIMUM_DATASECTION_SIZE; // minimum data section body size

        public bool TryParseEofHeader(ReadOnlySpan<byte> container, out EofHeader? header)
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ushort GetUInt16(ReadOnlySpan<byte> container, int offset) =>
                container.Slice(offset, TWO_BYTE_LENGTH).ReadEthUInt16();

            header = null;
            // we need to be able to parse header + minimum section lenghts
            if (container.Length < MINIMUM_SIZE)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                return false;
            }

            if (!container.StartsWith(MAGIC))
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code doesn't start with Magic byte sequence expected {MAGIC.ToHexString(true)} ");
                return false;
            }

            if (container[VERSION_OFFSET] != VERSION)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is not Eof version {VERSION}");
                return false;
            }

            Sizes sectionSizes = new();
            int[] codeSections = null;
            int[] containerSections = null;
            int pos = VERSION_OFFSET + 1;

            bool continueParsing = true;
            while (continueParsing && pos < container.Length)
            {
                Separator separator = (Separator)container[pos++];

                switch (separator)
                {
                    case Separator.KIND_TYPE:
                        if (container.Length < pos + TWO_BYTE_LENGTH)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                            return false;
                        }

                        sectionSizes.TypeSectionSize = GetUInt16(container, pos);
                        if (sectionSizes.TypeSectionSize < MINIMUM_TYPESECTION_SIZE)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, TypeSection Size must be at least 3, but found {sectionSizes.TypeSectionSize}");
                            return false;
                        }

                        pos += TWO_BYTE_LENGTH;
                        break;
                    case Separator.KIND_CODE:
                        if (sectionSizes.TypeSectionSize is null)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is not well fromated");
                            return false;
                        }

                        if (container.Length < pos + TWO_BYTE_LENGTH)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                            return false;
                        }

                        ushort numberOfCodeSections = GetUInt16(container, pos);
                        sectionSizes.CodeSectionSize = (ushort)(numberOfCodeSections * TWO_BYTE_LENGTH);
                        if (numberOfCodeSections > MAXIMUM_NUM_CODE_SECTIONS)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, code sections count must not exceed {MAXIMUM_NUM_CODE_SECTIONS}");
                            return false;
                        }

                        if (container.Length < pos + TWO_BYTE_LENGTH + TWO_BYTE_LENGTH * numberOfCodeSections)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                            return false;
                        }

                        codeSections = new int[numberOfCodeSections];
                        int CODESECTION_HEADER_PREFIX_SIZE = pos + TWO_BYTE_LENGTH;
                        for (ushort i = 0; i < numberOfCodeSections; i++)
                        {
                            int currentCodeSizeOffset = CODESECTION_HEADER_PREFIX_SIZE + i * EvmObjectFormat.TWO_BYTE_LENGTH; // offset of pos'th code size
                            int codeSectionSize = GetUInt16(container, currentCodeSizeOffset);

                            if (codeSectionSize == 0)
                            {
                                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Empty Code Section are not allowed, CodeSectionSize must be > 0 but found {codeSectionSize}");
                                return false;
                            }

                            codeSections[i] = codeSectionSize;
                        }

                        pos += TWO_BYTE_LENGTH + TWO_BYTE_LENGTH * numberOfCodeSections;
                        break;
                    case Separator.KIND_CONTAINER:
                        if (sectionSizes.CodeSectionSize is null)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is not well fromated");
                            return false;
                        }

                        if (container.Length < pos + TWO_BYTE_LENGTH)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                            return false;
                        }

                        ushort numberOfContainerSections = GetUInt16(container, pos);
                        sectionSizes.ContainerSectionSize = (ushort)(numberOfContainerSections * TWO_BYTE_LENGTH);
                        if (numberOfContainerSections is > MAXIMUM_NUM_CONTAINER_SECTIONS or 0)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, code sections count must not exceed {MAXIMUM_NUM_CONTAINER_SECTIONS}");
                            return false;
                        }

                        if (container.Length < pos + TWO_BYTE_LENGTH + TWO_BYTE_LENGTH * numberOfContainerSections)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                            return false;
                        }

                        containerSections = new int[numberOfContainerSections];
                        int CONTAINER_SECTION_HEADER_PREFIX_SIZE = pos + TWO_BYTE_LENGTH;
                        for (ushort i = 0; i < numberOfContainerSections; i++)
                        {
                            int currentContainerSizeOffset = CONTAINER_SECTION_HEADER_PREFIX_SIZE + i * EvmObjectFormat.TWO_BYTE_LENGTH; // offset of pos'th code size
                            int containerSectionSize = GetUInt16(container, currentContainerSizeOffset);

                            if (containerSectionSize == 0)
                            {
                                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Empty Container Section are not allowed, containerSectionSize must be > 0 but found {containerSectionSize}");
                                return false;
                            }

                            containerSections[i] = containerSectionSize;
                        }

                        pos += TWO_BYTE_LENGTH + TWO_BYTE_LENGTH * numberOfContainerSections;
                        break;
                    case Separator.KIND_DATA:
                        if (sectionSizes.CodeSectionSize is null)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is not well fromated");
                            return false;
                        }

                        if (container.Length < pos + TWO_BYTE_LENGTH)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                            return false;
                        }

                        sectionSizes.DataSectionSize = GetUInt16(container, pos);

                        pos += TWO_BYTE_LENGTH;
                        break;
                    case Separator.TERMINATOR:
                        if (container.Length < pos + ONE_BYTE_LENGTH)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                            return false;
                        }

                        continueParsing = false;
                        break;
                    default:
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code header is not well formatted");
                        return false;
                }
            }

            if (sectionSizes.TypeSectionSize is null || sectionSizes.CodeSectionSize is null || sectionSizes.DataSectionSize is null)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is not well formatted");
                return false;
            }

            SectionHeader typeSectionSubHeader = new SectionHeader(pos, sectionSizes.TypeSectionSize.Value);
            CompoundSectionHeader codeSectionSubHeader = new CompoundSectionHeader(typeSectionSubHeader.EndOffset, codeSections);
            CompoundSectionHeader? containerSectionSubHeader = containerSections is null ? null
                : new CompoundSectionHeader(codeSectionSubHeader.EndOffset, containerSections);
            SectionHeader dataSectionSubHeader = new SectionHeader(containerSectionSubHeader?.EndOffset ?? codeSectionSubHeader.EndOffset, sectionSizes.DataSectionSize.Value);

            header = new EofHeader
            {
                Version = VERSION,
                PrefixSize = pos,
                TypeSection = typeSectionSubHeader,
                CodeSections = codeSectionSubHeader,
                ContainerSections = containerSectionSubHeader,
                DataSection = dataSectionSubHeader,
            };

            return true;
        }
        public bool TryParseEofHeader2(ReadOnlySpan<byte> container, out EofHeader? header)
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ushort GetUInt16(ReadOnlySpan<byte> container, int offset) =>
                container.Slice(offset, TWO_BYTE_LENGTH).ReadEthUInt16();

            header = null;
            // we need to be able to parse header + minimum section lenghts
            if (container.Length < MINIMUM_SIZE)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                return false;
            }

            if (!container.StartsWith(MAGIC))
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code doesn't start with Magic byte sequence expected {MAGIC.ToHexString(true)} ");
                return false;
            }

            if (container[VERSION_OFFSET] != VERSION)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is not Eof version {VERSION}");
                return false;
            }

            Sizes sectionSizes = new();

            int TYPESECTION_HEADER_STARTOFFSET = VERSION_OFFSET + ONE_BYTE_LENGTH;
            int TYPESECTION_HEADER_ENDOFFSET = TYPESECTION_HEADER_STARTOFFSET + TWO_BYTE_LENGTH;
            if (container[TYPESECTION_HEADER_STARTOFFSET] != (byte)Separator.KIND_TYPE)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is not Eof version {VERSION}");
                return false;
            }
            sectionSizes.TypeSectionSize = GetUInt16(container, TYPESECTION_HEADER_STARTOFFSET + ONE_BYTE_LENGTH);
            if (sectionSizes.TypeSectionSize < MINIMUM_TYPESECTION_SIZE)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, TypeSection Size must be at least 3, but found {sectionSizes.TypeSectionSize}");
                return false;
            }

            int CODESECTION_HEADER_STARTOFFSET = TYPESECTION_HEADER_ENDOFFSET + ONE_BYTE_LENGTH;
            if (container[CODESECTION_HEADER_STARTOFFSET] != (byte)Separator.KIND_CODE)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code header is not well formatted");
                return false;
            }

            ushort numberOfCodeSections = GetUInt16(container, CODESECTION_HEADER_STARTOFFSET + ONE_BYTE_LENGTH);
            sectionSizes.CodeSectionSize = (ushort)(numberOfCodeSections * TWO_BYTE_LENGTH);
            if (numberOfCodeSections > MAXIMUM_NUM_CODE_SECTIONS)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, code sections count must not exceed {MAXIMUM_NUM_CODE_SECTIONS}");
                return false;
            }

            int[] codeSections = new int[numberOfCodeSections];
            int CODESECTION_HEADER_PREFIX_SIZE = CODESECTION_HEADER_STARTOFFSET + TWO_BYTE_LENGTH;
            for (ushort pos = 0; pos < numberOfCodeSections; pos++)
            {
                int currentCodeSizeOffset = CODESECTION_HEADER_PREFIX_SIZE + pos * EvmObjectFormat.TWO_BYTE_LENGTH; // offset of pos'th code size
                int codeSectionSize = GetUInt16(container, currentCodeSizeOffset + ONE_BYTE_LENGTH);

                if (codeSectionSize == 0)
                {
                    if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Empty Code Section are not allowed, CodeSectionSize must be > 0 but found {codeSectionSize}");
                    return false;
                }

                codeSections[pos] = codeSectionSize;
            }
            var CODESECTION_HEADER_ENDOFFSET = CODESECTION_HEADER_PREFIX_SIZE + numberOfCodeSections * TWO_BYTE_LENGTH;

            int CONTAINERSECTION_HEADER_STARTOFFSET = CODESECTION_HEADER_ENDOFFSET + ONE_BYTE_LENGTH;
            int? CONTAINERSECTION_HEADER_ENDOFFSET = null;
            int[] containerSections = null;
            if (container[CONTAINERSECTION_HEADER_STARTOFFSET] == (byte)Separator.KIND_CONTAINER)
            {
                ushort numberOfContainerSections = GetUInt16(container, CONTAINERSECTION_HEADER_STARTOFFSET + ONE_BYTE_LENGTH);
                sectionSizes.ContainerSectionSize = (ushort)(numberOfContainerSections * TWO_BYTE_LENGTH);
                if (numberOfContainerSections is > MAXIMUM_NUM_CONTAINER_SECTIONS or 0)
                {
                    if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, code sections count must not exceed {MAXIMUM_NUM_CONTAINER_SECTIONS}");
                    return false;
                }

                containerSections = new int[numberOfContainerSections];
                int CONTAINER_SECTION_HEADER_PREFIX_SIZE = CONTAINERSECTION_HEADER_STARTOFFSET + TWO_BYTE_LENGTH;
                for (ushort pos = 0; pos < numberOfContainerSections; pos++)
                {
                    int currentContainerSizeOffset = CONTAINER_SECTION_HEADER_PREFIX_SIZE + pos * EvmObjectFormat.TWO_BYTE_LENGTH; // offset of pos'th code size
                    int containerSectionSize = GetUInt16(container, currentContainerSizeOffset + ONE_BYTE_LENGTH);

                    if (containerSectionSize == 0)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Empty Container Section are not allowed, containerSectionSize must be > 0 but found {containerSectionSize}");
                        return false;
                    }

                    containerSections[pos] = containerSectionSize;
                }
                CONTAINERSECTION_HEADER_ENDOFFSET = CONTAINER_SECTION_HEADER_PREFIX_SIZE + numberOfContainerSections * TWO_BYTE_LENGTH;
            }


            int DATASECTION_HEADER_STARTOFFSET = CONTAINERSECTION_HEADER_ENDOFFSET + ONE_BYTE_LENGTH ?? CONTAINERSECTION_HEADER_STARTOFFSET;
            int DATASECTION_HEADER_ENDOFFSET = DATASECTION_HEADER_STARTOFFSET + TWO_BYTE_LENGTH;
            if (container[DATASECTION_HEADER_STARTOFFSET] != (byte)Separator.KIND_DATA)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code header is not well formatted");
                return false;
            }
            sectionSizes.DataSectionSize = GetUInt16(container, DATASECTION_HEADER_STARTOFFSET + ONE_BYTE_LENGTH);


            int HEADER_TERMINATOR_OFFSET = DATASECTION_HEADER_ENDOFFSET + ONE_BYTE_LENGTH;
            if (container[HEADER_TERMINATOR_OFFSET] != (byte)Separator.TERMINATOR)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code header is not well formatted");
                return false;
            }

            SectionHeader typeSectionHeader = new
            (
                start: HEADER_TERMINATOR_OFFSET + ONE_BYTE_LENGTH,
                size: sectionSizes.TypeSectionSize.Value
            );

            CompoundSectionHeader codeSectionHeader = new(
                start: typeSectionHeader.EndOffset,
                subSectionsSizes: codeSections
            );

            CompoundSectionHeader? containerSectionHeader = containerSections is null ? null
                : new(
                        start: codeSectionHeader.EndOffset,
                        subSectionsSizes: containerSections
                    );

            SectionHeader dataSectionHeader = new(
                start: containerSectionHeader?.EndOffset ?? codeSectionHeader.EndOffset,
                size: sectionSizes.DataSectionSize.Value
            );

            header = new EofHeader
            {
                Version = VERSION,
                PrefixSize = HEADER_TERMINATOR_OFFSET,
                TypeSection = typeSectionHeader,
                CodeSections = codeSectionHeader,
                ContainerSections = containerSectionHeader,
                DataSection = dataSectionHeader,
            };

            return true;
        }

        public bool ValidateBody(ReadOnlySpan<byte> container, EofHeader header, ValidationStrategy strategy)
        {
            int startOffset = header.TypeSection.Start;
            int endOffset = header.DataSection.Start;
            int calculatedCodeLength =
                    header.TypeSection.Size
                + header.CodeSections.Size
                + (header.ContainerSections?.Size ?? 0);
            CompoundSectionHeader codeSections = header.CodeSections;
            ReadOnlySpan<byte> contractBody = container[startOffset..endOffset];
            ReadOnlySpan<byte> dataBody = container[endOffset..];
            var typeSection = header.TypeSection;
            (int typeSectionStart, int typeSectionSize) = (typeSection.Start, typeSection.Size);

            if (header.ContainerSections?.Count > MAXIMUM_NUM_CONTAINER_SECTIONS)
            {
                // move this check where `header.ExtraContainers.Count` is parsed
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, initcode Containers bount must be less than {MAXIMUM_NUM_CONTAINER_SECTIONS} but found {header.ContainerSections?.Count}");
                return false;
            }

            if (contractBody.Length != calculatedCodeLength)
            {
                if (Logger.IsTrace) Logger.Trace("EOF: Eof{VERSION}, SectionSizes indicated in bundled header are incorrect, or ContainerCode is incomplete");
                return false;
            }

            if (strategy.HasFlag(ValidationStrategy.ValidateFullBody) && header.DataSection.Size > dataBody.Length)
            {
                if (Logger.IsTrace) Logger.Trace("EOF: Eof{VERSION}, DataSectionSize indicated in bundled header are incorrect, or DataSection is wrong");
                return false;
            }

            if (!strategy.HasFlag(ValidationStrategy.AllowTrailingBytes) && strategy.HasFlag(ValidationStrategy.ValidateFullBody) && header.DataSection.Size != dataBody.Length)
            {
                if (Logger.IsTrace) Logger.Trace("EOF: Eof{VERSION}, DataSectionSize indicated in bundled header are incorrect, or DataSection is wrong");
                return false;
            }

            if (codeSections.Count == 0 || codeSections.SubSectionsSizes.Any(size => size == 0))
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, CodeSection size must follow a CodeSection, CodeSection length was {codeSections.Count}");
                return false;
            }

            if (codeSections.Count != (typeSectionSize / MINIMUM_TYPESECTION_SIZE))
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code Sections count must match TypeSection count, CodeSection count was {codeSections.Count}, expected {typeSectionSize / MINIMUM_TYPESECTION_SIZE}");
                return false;
            }

            ReadOnlySpan<byte> typesection = container.Slice(typeSectionStart, typeSectionSize);
            if (!ValidateTypeSection(typesection))
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, invalid typesection found");
                return false;
            }

            Span<bool> visitedSections = stackalloc bool[header.CodeSections.Count];
            Span<byte> visitedContainerSections = stackalloc byte[header.ContainerSections is null ? 1 : 1 + header.ContainerSections.Value.Count];
            visitedContainerSections[0] = strategy.HasFlag(ValidationStrategy.ValidateInitcodeMode)
                ? (byte)ValidationStrategy.ValidateInitcodeMode
                : (strategy.HasFlag(ValidationStrategy.ValidateRuntimeMode)
                    ? (byte)ValidationStrategy.ValidateRuntimeMode
                    : (byte)0);

            visitedSections.Clear();

            Queue<ushort> validationQueue = new Queue<ushort>();
            validationQueue.Enqueue(0);

            while (validationQueue.TryDequeue(out ushort sectionIdx))
            {
                if (visitedSections[sectionIdx])
                {
                    continue;
                }

                visitedSections[sectionIdx] = true;
                var codeSection = header.CodeSections[sectionIdx];

                ReadOnlySpan<byte> code = container.Slice(header.CodeSections.Start + codeSection.Start, codeSection.Size);
                if (!ValidateInstructions(sectionIdx, strategy, typesection, code, header, container, validationQueue, ref visitedContainerSections))
                {
                    return false;
                }
            }

            bool HasNoNonReachableSections =
                visitedSections[..header.CodeSections.Count].Contains(false)
                || (header.ContainerSections is not null && visitedContainerSections[1..header.ContainerSections.Value.Count].Contains((byte)0));

            return !HasNoNonReachableSections;
        }

        bool ValidateTypeSection(ReadOnlySpan<byte> types)
        {
            if (types[INPUTS_OFFSET] != 0 || types[OUTPUTS_OFFSET] != NON_RETURNING)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, first 2 bytes of type section must be 0s");
                return false;
            }

            if (types.Length % MINIMUM_TYPESECTION_SIZE != 0)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, type section length must be a product of {MINIMUM_TYPESECTION_SIZE}");
                return false;
            }

            for (int offset = 0; offset < types.Length; offset += MINIMUM_TYPESECTION_SIZE)
            {
                byte inputCount = types[offset + INPUTS_OFFSET];
                byte outputCount = types[offset + OUTPUTS_OFFSET];
                ushort maxStackHeight = types.Slice(offset + MAX_STACK_HEIGHT_OFFSET, MAX_STACK_HEIGHT_LENGTH).ReadEthUInt16();

                if (inputCount > INPUTS_MAX)
                {
                    if (Logger.IsTrace) Logger.Trace("EOF: Eof{VERSION}, Too many inputs");
                    return false;
                }

                if (outputCount > OUTPUTS_MAX && outputCount != NON_RETURNING)
                {
                    if (Logger.IsTrace) Logger.Trace("EOF: Eof{VERSION}, Too many outputs");
                    return false;
                }

                if (maxStackHeight > MAX_STACK_HEIGHT)
                {
                    if (Logger.IsTrace) Logger.Trace("EOF: Eof{VERSION}, Stack depth too high");
                    return false;
                }
            }
            return true;
        }

        bool ValidateInstructions(ushort sectionId, ValidationStrategy strategy, ReadOnlySpan<byte> typesection, ReadOnlySpan<byte> code, in EofHeader header, in ReadOnlySpan<byte> container, Queue<ushort> worklist, ref Span<byte> visitedContainers)
        {
            int length = (code.Length / BYTE_BIT_COUNT) + 1;
            byte[] codeBitmapArray = ArrayPool<byte>.Shared.Rent(length);
            byte[] jumpDestsArray = ArrayPool<byte>.Shared.Rent(length);

            try
            {
                // ArrayPool may return a larger array than requested, so we need to slice it to the actual length
                Span<byte> codeBitmap = codeBitmapArray.AsSpan(0, length);
                Span<byte> jumpDests = jumpDestsArray.AsSpan(0, length);
                // ArrayPool may return a larger array than requested, so we need to slice it to the actual length
                codeBitmap.Clear();
                jumpDests.Clear();

                int pos;
                for (pos = 0; pos < code.Length;)
                {
                    Instruction opcode = (Instruction)code[pos];
                    int postInstructionByte = pos + 1;

                    if (opcode is Instruction.RETURN or Instruction.STOP)
                    {
                        if (strategy.HasFlag(ValidationStrategy.ValidateInitcodeMode))
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, CodeSection contains {opcode} opcode");
                            return false;
                        }
                        else
                        {
                            if (visitedContainers[0] == (byte)ValidationStrategy.ValidateInitcodeMode)
                            {
                                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, CodeSection cannot contain {opcode} opcode");
                                return false;
                            }
                            else
                            {
                                visitedContainers[0] = (byte)ValidationStrategy.ValidateRuntimeMode;
                            }
                        }
                    }

                    if (opcode is Instruction.RETURNCONTRACT)
                    {
                        if (strategy.HasFlag(ValidationStrategy.ValidateRuntimeMode))
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, CodeSection contains {opcode} opcode");
                            return false;
                        }
                        else
                        {
                            if (visitedContainers[0] == (byte)ValidationStrategy.ValidateRuntimeMode)
                            {
                                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, CodeSection cannot contain {opcode} opcode");
                                return false;
                            }
                            else
                            {
                                visitedContainers[0] = (byte)ValidationStrategy.ValidateInitcodeMode;
                            }
                        }
                    }

                    if (!opcode.IsValid(IsEofContext: true))
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, CodeSection contains undefined opcode {opcode}");
                        return false;
                    }

                    if (opcode is Instruction.RJUMP or Instruction.RJUMPI)
                    {
                        if (postInstructionByte + TWO_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {opcode.FastToString()} Argument underflow");
                            return false;
                        }

                        var offset = code.Slice(postInstructionByte, TWO_BYTE_LENGTH).ReadEthInt16();
                        var rjumpdest = offset + TWO_BYTE_LENGTH + postInstructionByte;

                        if (rjumpdest < 0 || rjumpdest >= code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {opcode.FastToString()} Destination outside of Code bounds");
                            return false;
                        }

                        BitmapHelper.HandleNumbits(ONE_BYTE_LENGTH, jumpDests, ref rjumpdest);
                        BitmapHelper.HandleNumbits(TWO_BYTE_LENGTH, codeBitmap, ref postInstructionByte);
                    }

                    if (opcode is Instruction.JUMPF)
                    {
                        if (postInstructionByte + TWO_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.JUMPF} Argument underflow");
                            return false;
                        }

                        var targetSectionId = code.Slice(postInstructionByte, TWO_BYTE_LENGTH).ReadEthUInt16();

                        if (targetSectionId >= header.CodeSections.Count)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.JUMPF} to unknown code section");
                            return false;
                        }

                        byte targetSectionOutputCount = typesection[targetSectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
                        byte currentSectionOutputCount = typesection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
                        bool isTargetSectionNonReturning = typesection[targetSectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET] == 0x80;

                        if (!isTargetSectionNonReturning && currentSectionOutputCount < targetSectionOutputCount)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.JUMPF} to code section with more outputs");
                            return false;
                        }

                        worklist.Enqueue(targetSectionId);
                        BitmapHelper.HandleNumbits(TWO_BYTE_LENGTH, codeBitmap, ref postInstructionByte);
                    }

                    if (opcode is Instruction.DUPN or Instruction.SWAPN or Instruction.EXCHANGE)
                    {
                        if (postInstructionByte + ONE_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {opcode.FastToString()} Argument underflow");
                            return false;
                        }
                        BitmapHelper.HandleNumbits(ONE_BYTE_LENGTH, codeBitmap, ref postInstructionByte);

                    }

                    if (opcode is Instruction.RJUMPV)
                    {
                        if (postInstructionByte + ONE_BYTE_LENGTH + TWO_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RJUMPV} Argument underflow");
                            return false;
                        }

                        ushort count = (ushort)(code[postInstructionByte] + 1);
                        if (count < MINIMUMS_ACCEPTABLE_JUMPV_JUMPTABLE_LENGTH)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RJUMPV} jumptable must have at least 1 entry");
                            return false;
                        }

                        if (postInstructionByte + ONE_BYTE_LENGTH + count * TWO_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RJUMPV} jumptable underflow");
                            return false;
                        }

                        var immediateValueSize = ONE_BYTE_LENGTH + count * TWO_BYTE_LENGTH;
                        for (int j = 0; j < count; j++)
                        {
                            var offset = code.Slice(postInstructionByte + ONE_BYTE_LENGTH + j * TWO_BYTE_LENGTH, TWO_BYTE_LENGTH).ReadEthInt16();
                            var rjumpdest = offset + immediateValueSize + postInstructionByte;
                            if (rjumpdest < 0 || rjumpdest >= code.Length)
                            {
                                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RJUMPV} Destination outside of Code bounds");
                                return false;
                            }
                            BitmapHelper.HandleNumbits(ONE_BYTE_LENGTH, jumpDests, ref rjumpdest);
                        }
                        BitmapHelper.HandleNumbits(immediateValueSize, codeBitmap, ref postInstructionByte);
                    }

                    if (opcode is Instruction.CALLF)
                    {
                        if (postInstructionByte + TWO_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.CALLF} Argument underflow");
                            return false;
                        }

                        ushort targetSectionId = code.Slice(postInstructionByte, TWO_BYTE_LENGTH).ReadEthUInt16();

                        if (targetSectionId >= header.CodeSections.Count)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.CALLF} Invalid Section Id");
                            return false;
                        }

                        byte targetSectionOutputCount = typesection[targetSectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
                        if (targetSectionOutputCount == 0x80)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.CALLF} into non-returning function");
                            return false;
                        }

                        worklist.Enqueue(targetSectionId);
                        BitmapHelper.HandleNumbits(TWO_BYTE_LENGTH, codeBitmap, ref postInstructionByte);
                    }

                    if (opcode is Instruction.RETF && typesection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET] == 0x80)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, non returning sections are not allowed to use opcode {Instruction.RETF}");
                        return false;
                    }

                    if (opcode is Instruction.DATALOADN)
                    {
                        if (postInstructionByte + TWO_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.DATALOADN} Argument underflow");
                            return false;
                        }

                        ushort dataSectionOffset = code.Slice(postInstructionByte, TWO_BYTE_LENGTH).ReadEthUInt16();

                        if (dataSectionOffset + 32 > header.DataSection.Size)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.DATALOADN}'s immediate argument must be less than datasection.Length / 32 i.e: {header.DataSection.Size / 32}");
                            return false;
                        }
                        BitmapHelper.HandleNumbits(TWO_BYTE_LENGTH, codeBitmap, ref postInstructionByte);
                    }

                    if (opcode is Instruction.RETURNCONTRACT)
                    {
                        if (postInstructionByte + ONE_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RETURNCONTRACT} Argument underflow");
                            return false;
                        }

                        ushort runtimeContainerId = code[postInstructionByte];
                        if (runtimeContainerId >= header.ContainerSections?.Count)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RETURNCONTRACT}'s immediate argument must be less than containersection.Count i.e: {header.ContainerSections?.Count}");
                            return false;
                        }

                        if (visitedContainers[runtimeContainerId + 1] != 0 && visitedContainers[runtimeContainerId + 1] != (byte)ValidationStrategy.ValidateRuntimeMode)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RETURNCONTRACT}'s target container can only be a runtime mode bytecode");
                            return false;
                        }

                        visitedContainers[runtimeContainerId + 1] = (byte)ValidationStrategy.ValidateRuntimeMode;
                        ReadOnlySpan<byte> subcontainer = container.Slice(header.ContainerSections.Value.Start + header.ContainerSections.Value[runtimeContainerId].Start, header.ContainerSections.Value[runtimeContainerId].Size);

                        if (!IsValidEof(subcontainer, ValidationStrategy.ValidateRuntimeMode, out _))
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RETURNCONTRACT}'s immediate must be a valid Eof");
                            return false;
                        }

                        BitmapHelper.HandleNumbits(ONE_BYTE_LENGTH, codeBitmap, ref postInstructionByte);
                    }

                    if (opcode is Instruction.EOFCREATE)
                    {
                        if (postInstructionByte + ONE_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.EOFCREATE} Argument underflow");
                            return false;
                        }

                        byte initcodeSectionId = code[postInstructionByte];
                        BitmapHelper.HandleNumbits(ONE_BYTE_LENGTH, codeBitmap, ref postInstructionByte);

                        if (initcodeSectionId >= header.ContainerSections?.Count)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.EOFCREATE}'s immediate must falls within the Containers' range available, i.e: {header.CodeSections.Count}");
                            return false;
                        }

                        if (visitedContainers[initcodeSectionId + 1] != 0 && visitedContainers[initcodeSectionId + 1] != (byte)ValidationStrategy.ValidateInitcodeMode)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.EOFCREATE}'s target container can only be a initcode mode bytecode");
                            return false;
                        }

                        visitedContainers[initcodeSectionId + 1] = (byte)ValidationStrategy.ValidateInitcodeMode;
                        ReadOnlySpan<byte> subcontainer = container.Slice(header.ContainerSections.Value.Start + header.ContainerSections.Value[initcodeSectionId].Start, header.ContainerSections.Value[initcodeSectionId].Size);
                        if (!IsValidEof(subcontainer, ValidationStrategy.ValidateInitcodeMode | ValidationStrategy.ValidateFullBody, out _))
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.EOFCREATE}'s immediate must be a valid Eof");
                            return false;
                        }

                    }

                    if (opcode is >= Instruction.PUSH0 and <= Instruction.PUSH32)
                    {
                        int len = opcode - Instruction.PUSH0;
                        if (postInstructionByte + len > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {opcode.FastToString()} PC Reached out of bounds");
                            return false;
                        }
                        BitmapHelper.HandleNumbits(len, codeBitmap, ref postInstructionByte);
                    }
                    pos = postInstructionByte;
                }

                if (pos > code.Length)
                {
                    if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, PC Reached out of bounds");
                    return false;
                }

                bool result = !BitmapHelper.CheckCollision(codeBitmap, jumpDests);
                if (!result)
                {
                    if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Invalid Jump destination");
                }

                if (!ValidateStackState(sectionId, code, typesection))
                {
                    return false;
                }
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(codeBitmapArray);
                ArrayPool<byte>.Shared.Return(jumpDestsArray);
            }
        }
        public bool ValidateStackState(int sectionId, in ReadOnlySpan<byte> code, in ReadOnlySpan<byte> typesection)
        {
            StackBounds[] recordedStackHeight = ArrayPool<StackBounds>.Shared.Rent(code.Length);
            recordedStackHeight.Fill(new StackBounds());

            try
            {
                ushort suggestedMaxHeight = typesection.Slice(sectionId * MINIMUM_TYPESECTION_SIZE + TWO_BYTE_LENGTH, TWO_BYTE_LENGTH).ReadEthUInt16();

                ushort currrentSectionOutputs = typesection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET] == 0x80 ? (ushort)0 : typesection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
                short peakStackHeight = typesection[sectionId * MINIMUM_TYPESECTION_SIZE + INPUTS_OFFSET];

                int unreachedBytes = code.Length;
                bool isTargetSectionNonReturning = false;

                int targetMaxStackHeight = 0;
                int programCounter = 0;
                recordedStackHeight[0].Max = peakStackHeight;
                recordedStackHeight[0].Min = peakStackHeight;
                StackBounds currentStackBounds = recordedStackHeight[0];

                while (programCounter < code.Length)
                {
                    Instruction opcode = (Instruction)code[programCounter];
                    (ushort? inputs, ushort? outputs, ushort? immediates) = opcode.StackRequirements();

                    ushort posPostInstruction = (ushort)(programCounter + 1);
                    if (posPostInstruction > code.Length)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, PC Reached out of bounds");
                        return false;
                    }

                    switch (opcode)
                    {
                        case Instruction.CALLF or Instruction.JUMPF:
                            ushort targetSectionId = code.Slice(posPostInstruction, immediates.Value).ReadEthUInt16();
                            inputs = typesection[targetSectionId * MINIMUM_TYPESECTION_SIZE + INPUTS_OFFSET];

                            outputs = typesection[targetSectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
                            isTargetSectionNonReturning = typesection[targetSectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET] == 0x80;
                            outputs = (ushort)(isTargetSectionNonReturning ? 0 : outputs);
                            targetMaxStackHeight = typesection.Slice(targetSectionId * MINIMUM_TYPESECTION_SIZE + MAX_STACK_HEIGHT_OFFSET, TWO_BYTE_LENGTH).ReadEthUInt16();

                            if (MAX_STACK_HEIGHT - targetMaxStackHeight + inputs < currentStackBounds.Max)
                            {
                                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, stack head during callf must not exceed {MAX_STACK_HEIGHT}");
                                return false;
                            }

                            if (opcode is Instruction.JUMPF && !isTargetSectionNonReturning && !(currrentSectionOutputs + inputs - outputs == currentStackBounds.Min && currentStackBounds.BoundsEqual()))
                            {
                                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Stack State invalid, required height {currrentSectionOutputs + inputs - outputs} but found {currentStackBounds.Max}");
                                return false;
                            }
                            break;
                        case Instruction.DUPN:
                            int imm_n = 1 + code[posPostInstruction];
                            inputs = (ushort)(imm_n);
                            outputs = (ushort)(inputs + 1);
                            break;
                        case Instruction.SWAPN:
                            imm_n = 1 + code[posPostInstruction];
                            outputs = inputs = (ushort)(1 + imm_n);
                            break;
                        case Instruction.EXCHANGE:
                            imm_n = 1 + (byte)(code[posPostInstruction] >> 4);
                            int imm_m = 1 + (byte)(code[posPostInstruction] & 0x0F);
                            outputs = inputs = (ushort)(imm_n + imm_m + 1);
                            break;
                    }

                    if ((isTargetSectionNonReturning || opcode is not Instruction.JUMPF) && currentStackBounds.Min < inputs)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Stack Underflow required {inputs} but found {currentStackBounds.Min}");
                        return false;
                    }

                    if (!opcode.IsTerminating())
                    {
                        short delta = (short)(outputs - inputs);
                        currentStackBounds.Max += delta;
                        currentStackBounds.Min += delta;
                    }
                    peakStackHeight = Math.Max(peakStackHeight, currentStackBounds.Max);

                    switch (opcode)
                    {
                        case Instruction.RETF:
                            {
                                var expectedHeight = typesection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
                                if (expectedHeight != currentStackBounds.Min || !currentStackBounds.BoundsEqual())
                                {
                                    if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Stack state invalid required height {expectedHeight} but found {currentStackBounds.Min}");
                                    return false;
                                }
                                break;
                            }
                        case Instruction.RJUMP or Instruction.RJUMPI:
                            {
                                short offset = code.Slice(programCounter + 1, immediates.Value).ReadEthInt16();
                                int jumpDestination = posPostInstruction + immediates.Value + offset;

                                if (opcode is Instruction.RJUMPI)
                                {
                                    recordedStackHeight[posPostInstruction + immediates.Value].Combine(currentStackBounds);
                                }

                                if (jumpDestination > programCounter)
                                {
                                    recordedStackHeight[jumpDestination].Combine(currentStackBounds);
                                }
                                else
                                {
                                    if (recordedStackHeight[jumpDestination] != currentStackBounds)
                                    {
                                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Stack state invalid at {jumpDestination}");
                                        return false;
                                    }
                                }

                                break;
                            }
                        case Instruction.RJUMPV:
                            {
                                var count = code[posPostInstruction] + 1;
                                immediates = (ushort)(count * TWO_BYTE_LENGTH + ONE_BYTE_LENGTH);
                                for (short j = 0; j < count; j++)
                                {
                                    int case_v = posPostInstruction + ONE_BYTE_LENGTH + j * TWO_BYTE_LENGTH;
                                    int offset = code.Slice(case_v, TWO_BYTE_LENGTH).ReadEthInt16();
                                    int jumpDestination = posPostInstruction + immediates.Value + offset;
                                    if (jumpDestination > programCounter)
                                    {
                                        recordedStackHeight[jumpDestination].Combine(currentStackBounds);
                                    }
                                    else
                                    {
                                        if (recordedStackHeight[jumpDestination] != currentStackBounds)
                                        {
                                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Stack state invalid at {jumpDestination}");
                                            return false;
                                        }
                                    }
                                }

                                posPostInstruction += immediates.Value;
                                if (posPostInstruction > code.Length)
                                {
                                    if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, PC Reached out of bounds");
                                    return false;
                                }
                                break;
                            }
                    }

                    unreachedBytes -= 1 + immediates.Value;
                    programCounter += 1 + immediates.Value;

                    if (opcode.IsTerminating())
                    {
                        if (programCounter < code.Length)
                        {
                            currentStackBounds = recordedStackHeight[programCounter];
                        }
                    }
                    else
                    {
                        recordedStackHeight[programCounter].Combine(currentStackBounds);
                        currentStackBounds = recordedStackHeight[programCounter];
                    }
                }

                if (unreachedBytes != 0)
                {
                    if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, bytecode has unreachable segments");
                    return false;
                }

                if (peakStackHeight != suggestedMaxHeight)
                {
                    if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Suggested Max Stack height mismatches with actual Max, expected {suggestedMaxHeight} but found {peakStackHeight}");
                    return false;
                }

                bool result = peakStackHeight < MAX_STACK_HEIGHT;
                if (!result)
                {
                    if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, stack overflow exceeded max stack height of {MAX_STACK_HEIGHT} but found {peakStackHeight}");
                    return false;
                }
                return result;
            }
            finally
            {
                ArrayPool<StackBounds>.Shared.Return(recordedStackHeight);
            }
        }
    }
}
