// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FastEnumUtility;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

[assembly: InternalsVisibleTo("Nethermind.EofParser")]

namespace Nethermind.Evm.EOF;

internal static class EvmObjectFormat
{
    [StructLayout(LayoutKind.Sequential)]
    struct Worklet
    {
        public Worklet(ushort position, ushort stackHeight)
        { 
            Position = position;
            StackHeight = stackHeight;
        }
        public ushort Position;
        public ushort StackHeight;
    }

    private interface IEofVersionHandler
    {
        bool ValidateBody(ReadOnlySpan<byte> code, EofHeader header);
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
        if(container.Length >= MAGIC.Length + 1)
        {
            version = container[MAGIC.Length];
            return container.StartsWith(MAGIC);
        } else
        {
            version = 0;
            return false;
        }
        
    }

    public static bool IsEofn(ReadOnlySpan<byte> container, byte version) => container.Length >= MAGIC.Length + 1 && container.StartsWith(MAGIC) && container[MAGIC.Length] == version;

    public static bool IsValidEof(ReadOnlySpan<byte> container, bool validateSubContainers, [NotNullWhen(true)] out EofHeader? header)
    {
        if (container.Length > VERSION_OFFSET
            && _eofVersionHandlers.TryGetValue(container[VERSION_OFFSET], out IEofVersionHandler handler)
            && handler.TryParseEofHeader(container, out header))
        {
            EofHeader h = header.Value;
            if (handler.ValidateBody(container, h))
            {
                if(validateSubContainers && header?.ContainerSection?.Count > 0)
                {
                    int containerSize = header.Value.ContainerSection.Value.Count;

                    for (int i = 0; i < containerSize; i++)
                    {
                        ReadOnlySpan<byte> subContainer = container.Slice(header.Value.ContainerSection.Value.Start + header.Value.ContainerSection.Value[i].Start, header.Value.ContainerSection.Value[i].Size);
                        if(!IsValidEof(subContainer, validateSubContainers, out _))
                        {
                            return false;
                        }
                    }
                    return true;
                }
                return true;
            }
        }

        header = null;
        return false;
    }

    public static bool TryExtractHeader(ReadOnlySpan<byte> container, [NotNullWhen(true)] out EofHeader? header)
    {
        header = null;
        return container.Length > VERSION_OFFSET
               && _eofVersionHandlers.TryGetValue(container[VERSION_OFFSET], out IEofVersionHandler handler)
               && handler.TryParseEofHeader(container, out header);
    }

    public static byte GetCodeVersion(ReadOnlySpan<byte> container)
    {
        return container.Length <= VERSION_OFFSET
            ? byte.MinValue
            : container[VERSION_OFFSET];
    }

    internal class Eof1 : IEofVersionHandler
    {
        private ref struct Sizes
        {
            public ushort TypeSectionSize;
            public ushort CodeSectionSize;
            public ushort DataSectionSize;
            public ushort ContainerSectionSize;
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
        internal const byte MINIMUM_HEADER_SIZE = VERSION_OFFSET + MINIMUM_HEADER_SECTION_SIZE + MINIMUM_HEADER_SECTION_SIZE + TWO_BYTE_LENGTH + MINIMUM_HEADER_SECTION_SIZE + ONE_BYTE_LENGTH;
        internal const byte MINIMUM_TYPESECTION_SIZE = 4;
        internal const byte MINIMUM_CODESECTION_SIZE = 1;
        internal const byte MINIMUM_DATASECTION_SIZE = 0;
        internal const byte MINIMUM_CONTAINERSECTION_SIZE = 0;

        internal const byte BYTE_BIT_COUNT = 8; // indicates the length of the count immediate of jumpv
        internal const byte MINIMUMS_ACCEPTABLE_JUMPV_JUMPTABLE_LENGTH = 1; // indicates the length of the count immediate of jumpv

        internal const byte INPUTS_OFFSET = 0;
        internal const byte INPUTS_MAX = 0x7F;

        internal const byte OUTPUTS_OFFSET = INPUTS_OFFSET + 1;
        internal const byte OUTPUTS_MAX = 0x7F;
        internal const byte NON_RETURNING = 0x80;

        internal const byte MAX_STACK_HEIGHT_OFFSET = OUTPUTS_OFFSET + 1;
        internal const int MAX_STACK_HEIGHT_LENGTH = 2;
        internal const ushort MAX_STACK_HEIGHT = 0x3FF;

        internal const ushort MINIMUM_NUM_CODE_SECTIONS = 1;
        internal const ushort MAXIMUM_NUM_CODE_SECTIONS = 1024;
        internal const ushort MAXIMUM_NUM_CONTAINER_SECTIONS = 0x00FF;
        internal const ushort RETURN_STACK_MAX_HEIGHT = MAXIMUM_NUM_CODE_SECTIONS; // the size in the type sectionn allocated to each function section

        internal const ushort MINIMUM_SIZE = MINIMUM_HEADER_SIZE
                                            + MINIMUM_TYPESECTION_SIZE // minimum type section body size
                                            + MINIMUM_CODESECTION_SIZE // minimum code section body size
                                            + MINIMUM_DATASECTION_SIZE // minimum data section body size
                                            + MINIMUM_CONTAINERSECTION_SIZE; // minimum container section body size

        public bool TryParseEofHeader(ReadOnlySpan<byte> container, out EofHeader? header)
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ushort GetUInt16(ReadOnlySpan<byte> container, int offset) =>
                container.Slice(offset, TWO_BYTE_LENGTH).ReadEthUInt16();

            header = null;
            // we need to be able to parse header + minimum section lenghts
            if (container.Length < MINIMUM_SIZE)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Eof{VERSION}, Code is too small to be valid code");
                return false;
            }

            if (!container.StartsWith(MAGIC))
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Code doesn't start with Magic byte sequence expected {MAGIC.ToHexString(true)} ");
                return false;
            }

            if (container[VERSION_OFFSET] != VERSION)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Code is not Eof version {VERSION}");
                return false;
            }

            Sizes sectionSizes = new();

            int TYPESECTION_HEADER_STARTOFFSET = VERSION_OFFSET + ONE_BYTE_LENGTH;
            int TYPESECTION_HEADER_ENDOFFSET = TYPESECTION_HEADER_STARTOFFSET + TWO_BYTE_LENGTH;
            if (container[TYPESECTION_HEADER_STARTOFFSET] != (byte)Separator.KIND_TYPE)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Code is not Eof version {VERSION}");
                return false;
            }
            sectionSizes.TypeSectionSize = GetUInt16(container, TYPESECTION_HEADER_STARTOFFSET + ONE_BYTE_LENGTH);
            if (sectionSizes.TypeSectionSize < MINIMUM_TYPESECTION_SIZE)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : TypeSection Size must be at least 3, but found {sectionSizes.TypeSectionSize}");
                return false;
            }

            int CODESECTION_HEADER_STARTOFFSET = TYPESECTION_HEADER_ENDOFFSET + ONE_BYTE_LENGTH;
            if (container[CODESECTION_HEADER_STARTOFFSET] != (byte)Separator.KIND_CODE)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
                return false;
            }

            ushort numberOfCodeSections = GetUInt16(container, CODESECTION_HEADER_STARTOFFSET + ONE_BYTE_LENGTH);
            sectionSizes.CodeSectionSize = (ushort)(numberOfCodeSections * TWO_BYTE_LENGTH);
            if (numberOfCodeSections > MAXIMUM_NUM_CODE_SECTIONS)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : code sections count must not exceed {MAXIMUM_NUM_CODE_SECTIONS}");
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
                    if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Empty Code Section are not allowed, CodeSectionSize must be > 0 but found {codeSectionSize}");
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
                if (numberOfContainerSections > MAXIMUM_NUM_CONTAINER_SECTIONS)
                {
                    if (Logger.IsTrace) Logger.Trace($"EIP-3540 : code sections count must not exceed {MAXIMUM_NUM_CONTAINER_SECTIONS}");
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
                        if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Empty Code Section are not allowed, containerSectionSize must be > 0 but found {containerSectionSize}");
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
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
                return false;
            }
            sectionSizes.DataSectionSize = GetUInt16(container, DATASECTION_HEADER_STARTOFFSET + ONE_BYTE_LENGTH);


            int HEADER_TERMINATOR_OFFSET = DATASECTION_HEADER_ENDOFFSET + ONE_BYTE_LENGTH;
            if (container[HEADER_TERMINATOR_OFFSET] != (byte)Separator.TERMINATOR)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
                return false;
            }

            SectionHeader typeSectionHeader = new
            (
                Start: HEADER_TERMINATOR_OFFSET + ONE_BYTE_LENGTH,
                Size: sectionSizes.TypeSectionSize
            );

            CompoundSectionHeader codeSectionHeader = new(
                Start: typeSectionHeader.EndOffset,
                SubSectionsSizes: codeSections
            );

            CompoundSectionHeader? containerSectionHeader = containerSections is null ? null
                : new(
                        Start: codeSectionHeader.EndOffset,
                        SubSectionsSizes: containerSections
                    );

            SectionHeader dataSectionHeader = new(
                Start: containerSectionHeader?.EndOffset ?? codeSectionHeader.EndOffset,
                Size: sectionSizes.DataSectionSize
            );

            header = new EofHeader
            {
                Version = VERSION,
                PrefixSize = HEADER_TERMINATOR_OFFSET,
                TypeSection = typeSectionHeader,
                CodeSections = codeSectionHeader,
                ContainerSection = containerSectionHeader,
                DataSection = dataSectionHeader,
            };

            return true;
        }

        public bool ValidateBody(ReadOnlySpan<byte> container, EofHeader header)
        {
            int startOffset = header.TypeSection.Start;
            int calculatedCodeLength =
                    header.TypeSection.Size
                +   header.CodeSections.Size
                +   header.DataSection.Size
                +   (header.ContainerSection?.Size ?? 0);
            CompoundSectionHeader codeSections = header.CodeSections;
            ReadOnlySpan<byte> contractBody = container[startOffset..];
            (int typeSectionStart, ushort typeSectionSize) = header.TypeSection;

            if (header.ContainerSection?.Count > MAXIMUM_NUM_CONTAINER_SECTIONS)
            {
                // move this check where `header.ExtraContainers.Count` is parsed
                if (Logger.IsTrace) Logger.Trace($"EIP-XXXX : initcode Containers bount must be less than {MAXIMUM_NUM_CONTAINER_SECTIONS} but found {header.ContainerSection?.Count}");
                return false;
            }

            if (contractBody.Length != calculatedCodeLength)
            {
                if (Logger.IsTrace) Logger.Trace("EIP-3540 : SectionSizes indicated in bundled header are incorrect, or ContainerCode is incomplete");
                return false;
            }

            if (codeSections.Count == 0 || codeSections.SubSectionsSizes.Any(size => size == 0))
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : CodeSection size must follow a CodeSection, CodeSection length was {codeSections.Count}");
                return false;
            }

            if (codeSections.Count != (typeSectionSize / MINIMUM_TYPESECTION_SIZE))
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-4750: Code Sections count must match TypeSection count, CodeSection count was {codeSections.Count}, expected {typeSectionSize / MINIMUM_TYPESECTION_SIZE}");
                return false;
            }

            ReadOnlySpan<byte> typesection = container.Slice(typeSectionStart, typeSectionSize);
            if (!ValidateTypeSection(typesection))
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-4750: invalid typesection found");
                return false;
            }

            bool[] visitedSections = ArrayPool<bool>.Shared.Rent(header.CodeSections.Count);
            Queue<ushort> validationQueue = new Queue<ushort>();
            validationQueue.Enqueue(0);


            while (validationQueue.TryDequeue(out ushort sectionIdx))
            {
                if (visitedSections[sectionIdx])
                {
                    continue;
                }

                visitedSections[sectionIdx] = true;
                (int codeSectionStartOffset, int codeSectionSize) = header.CodeSections[sectionIdx];


                bool isNonReturning = typesection[sectionIdx * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET] == 0x80;
                ReadOnlySpan<byte> code = container.Slice(header.CodeSections.Start + codeSectionStartOffset, codeSectionSize);
                if (!ValidateInstructions(sectionIdx, isNonReturning, typesection, code, header, validationQueue, out ushort jumpsCount))
                {
                    ArrayPool<bool>.Shared.Return(visitedSections, true);
                    return false;
                }
            }

            var HasNoNonReachableCodeSections = visitedSections[..header.CodeSections.Count].All(id => id);
            ArrayPool<bool>.Shared.Return(visitedSections, true);

            return HasNoNonReachableCodeSections;
        }

        bool ValidateTypeSection(ReadOnlySpan<byte> types)
        {
            if (types[INPUTS_OFFSET] != 0 || types[OUTPUTS_OFFSET] != NON_RETURNING)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-4750: first 2 bytes of type section must be 0s");
                return false;
            }

            if (types.Length % MINIMUM_TYPESECTION_SIZE != 0)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-4750: type section length must be a product of {MINIMUM_TYPESECTION_SIZE}");
                return false;
            }

            for (int offset = 0; offset < types.Length; offset += MINIMUM_TYPESECTION_SIZE)
            {
                byte inputCount = types[offset + INPUTS_OFFSET];
                byte outputCount = types[offset + OUTPUTS_OFFSET];
                ushort maxStackHeight = types.Slice(offset + MAX_STACK_HEIGHT_OFFSET, MAX_STACK_HEIGHT_LENGTH).ReadEthUInt16();

                if (inputCount > INPUTS_MAX)
                {
                    if (Logger.IsTrace) Logger.Trace("EIP-3540 : Too many inputs");
                    return false;
                }

                if (outputCount > OUTPUTS_MAX && outputCount != NON_RETURNING)
                {
                    if (Logger.IsTrace) Logger.Trace("EIP-3540 : Too many outputs");
                    return false;
                }

                if (maxStackHeight > MAX_STACK_HEIGHT)
                {
                    if (Logger.IsTrace) Logger.Trace("EIP-3540 : Stack depth too high");
                    return false;
                }
            }
            return true;
        }

        bool ValidateInstructions(ushort sectionId, bool isNonReturning, ReadOnlySpan<byte> typesection, ReadOnlySpan<byte> code, in EofHeader header, Queue<ushort> worklist, out ushort jumpsCount)
        {
            byte[] codeBitmap = ArrayPool<byte>.Shared.Rent((code.Length / BYTE_BIT_COUNT) + 1);
            byte[] jumpdests = ArrayPool<byte>.Shared.Rent((code.Length / BYTE_BIT_COUNT) + 1);
            jumpsCount = 1;
            try
            {
                int pos;

                for (pos = 0; pos < code.Length;)
                {
                    Instruction opcode = (Instruction)code[pos];
                    int postInstructionByte = pos + 1;

                    if (!opcode.IsValid(IsEofContext: true))
                    {
                        if (Logger.IsTrace) Logger.Trace($"EIP-3670 : CodeSection contains undefined opcode {opcode}");
                        return false;
                    }

                    if (opcode is Instruction.RJUMP or Instruction.RJUMPI)
                    {
                        if (postInstructionByte + TWO_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-4200 : Static Relative Jump Argument underflow");
                            return false;
                        }

                        var offset = code.Slice(postInstructionByte, TWO_BYTE_LENGTH).ReadEthInt16();
                        var rjumpdest = offset + TWO_BYTE_LENGTH + postInstructionByte;

                        if (rjumpdest < 0 || rjumpdest >= code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-4200 : Static Relative Jump Destination outside of Code bounds");
                            return false;
                        }

                        jumpsCount += opcode is Instruction.RJUMP ? ONE_BYTE_LENGTH : TWO_BYTE_LENGTH;
                        BitmapHelper.HandleNumbits(ONE_BYTE_LENGTH, jumpdests, ref rjumpdest);
                        BitmapHelper.HandleNumbits(TWO_BYTE_LENGTH, codeBitmap, ref postInstructionByte);
                    }

                    if (opcode is Instruction.JUMPF)
                    {
                        if (postInstructionByte + TWO_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-6206 : JUMPF Argument underflow");
                            return false;
                        }

                        var targetSectionId = code.Slice(postInstructionByte, TWO_BYTE_LENGTH).ReadEthUInt16();

                        if (targetSectionId >= header.CodeSections.Count)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-6206 : JUMPF to unknown code section");
                            return false;
                        }

                        bool isTargetSectionNonReturning = typesection[targetSectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET] == 0x80;

                        if (isNonReturning && !isTargetSectionNonReturning)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-XXXX : JUMPF from non returning code-sections can only call non-returning sections");
                            return false;
                        }

                        worklist.Enqueue(targetSectionId);
                        BitmapHelper.HandleNumbits(TWO_BYTE_LENGTH, codeBitmap, ref postInstructionByte);
                    }

                    if (opcode is Instruction.DUPN or Instruction.SWAPN or Instruction.EXCHANGE)
                    {
                        if (postInstructionByte + ONE_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-663 : {opcode.FastToString()} Argument underflow");
                            return false;
                        }
                        BitmapHelper.HandleNumbits(ONE_BYTE_LENGTH, codeBitmap, ref postInstructionByte);

                    }

                    if (opcode is Instruction.RJUMPV)
                    {
                        if (postInstructionByte + TWO_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-4200 : Static Relative Jumpv Argument underflow");
                            return false;
                        }

                        ushort count = (ushort)(code[postInstructionByte] + 1);
                        jumpsCount += count;
                        if (count < MINIMUMS_ACCEPTABLE_JUMPV_JUMPTABLE_LENGTH)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-4200 : jumpv jumptable must have at least 1 entry");
                            return false;
                        }

                        if (postInstructionByte + ONE_BYTE_LENGTH + count * TWO_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-4200 : jumpv jumptable underflow");
                            return false;
                        }

                        var immediateValueSize = ONE_BYTE_LENGTH + count * TWO_BYTE_LENGTH;
                        for (int j = 0; j < count; j++)
                        {
                            var offset = code.Slice(postInstructionByte + ONE_BYTE_LENGTH + j * TWO_BYTE_LENGTH, TWO_BYTE_LENGTH).ReadEthInt16();
                            var rjumpdest = offset + immediateValueSize + postInstructionByte;
                            if (rjumpdest < 0 || rjumpdest >= code.Length)
                            {
                                if (Logger.IsTrace) Logger.Trace($"EIP-4200 : Static Relative Jumpv Destination outside of Code bounds");
                                return false;
                            }
                            BitmapHelper.HandleNumbits(ONE_BYTE_LENGTH, jumpdests, ref rjumpdest);
                        }
                        BitmapHelper.HandleNumbits(immediateValueSize, codeBitmap, ref postInstructionByte);
                    }

                    if (opcode is Instruction.CALLF)
                    {
                        if (postInstructionByte + TWO_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-4750 : CALLF Argument underflow");
                            return false;
                        }

                        ushort targetSectionId = code.Slice(postInstructionByte, TWO_BYTE_LENGTH).ReadEthUInt16();

                        if (targetSectionId >= header.CodeSections.Count)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-4750 : Invalid Section Id");
                            return false;
                        }

                        byte targetSectionOutputCount = typesection[targetSectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
                        if (targetSectionOutputCount == 0x80)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-XXXX : CALLF into non-returning function");
                            return false;
                        }

                        worklist.Enqueue(targetSectionId);
                        BitmapHelper.HandleNumbits(TWO_BYTE_LENGTH, codeBitmap, ref postInstructionByte);
                    }

                    if (opcode is Instruction.RETF && typesection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET] == 0x80)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EIP-XXXX : non returning sections are not allowed to use opcode {Instruction.RETF}");
                        return false;
                    }

                    if (opcode is Instruction.DATALOADN)
                    {
                        if (postInstructionByte + TWO_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-XXXX : DATALOADN Argument underflow");
                            return false;
                        }

                        ushort dataSectionOffset = code.Slice(postInstructionByte, TWO_BYTE_LENGTH).ReadEthUInt16();

                        if (dataSectionOffset * 32 >= header.DataSection.Size)
                        {

                            if (Logger.IsTrace) Logger.Trace($"EIP-XXXX : DATALOADN's immediate argument must be less than datasection.Length / 32 i.e : {header.DataSection.Size / 32}");
                            return false;
                        }
                        BitmapHelper.HandleNumbits(TWO_BYTE_LENGTH, codeBitmap, ref postInstructionByte);
                    }

                    if (opcode is Instruction.RETURNCONTRACT)
                    {
                        if (postInstructionByte + ONE_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-XXXX : DATALOADN Argument underflow");
                            return false;
                        }

                        ushort containerId = code[postInstructionByte];
                        if (containerId >= 0 && containerId < header.ContainerSection?.Count)
                        {

                            if (Logger.IsTrace) Logger.Trace($"EIP-XXXX : RETURNCONTRACT's immediate argument must be less than containersection.Count i.e : {header.ContainerSection?.Count}");
                            return false;
                        }
                        BitmapHelper.HandleNumbits(ONE_BYTE_LENGTH, codeBitmap, ref postInstructionByte);
                    }

                    if (opcode is Instruction.EOFCREATE)
                    {
                        if (postInstructionByte + ONE_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-XXXX : CREATE3 Argument underflow");
                            return false;
                        }

                        byte initcodeSectionId = code[postInstructionByte];
                        BitmapHelper.HandleNumbits(ONE_BYTE_LENGTH, codeBitmap, ref postInstructionByte);

                        if (initcodeSectionId >= header.ContainerSection?.Count)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-XXXX : CREATE3's immediate must falls within the Containers' range available, i.e : {header.CodeSections.Count}");
                            return false;
                        }
                    }

                    if (opcode is >= Instruction.PUSH0 and <= Instruction.PUSH32)
                    {
                        int len = opcode - Instruction.PUSH0;
                        if (postInstructionByte + len > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-3670 : PC Reached out of bounds");
                            return false;
                        }
                        BitmapHelper.HandleNumbits(len, codeBitmap, ref postInstructionByte);
                    }
                    pos = postInstructionByte;
                }

                if (pos > code.Length)
                {
                    if (Logger.IsTrace) Logger.Trace($"EIP-3670 : PC Reached out of bounds");
                    return false;
                }

                bool result = !BitmapHelper.CheckCollision(codeBitmap, jumpdests);
                if (!result)
                {
                    if (Logger.IsTrace) Logger.Trace($"EIP-4200 : Invalid Jump destination");
                }

                if (!ValidateStackState(sectionId, code, typesection, jumpsCount))
                {
                    return false;
                }
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(codeBitmap, true);
                ArrayPool<byte>.Shared.Return(jumpdests, true);
            }
        }
        public bool ValidateStackState(int sectionId, in ReadOnlySpan<byte> code, in ReadOnlySpan<byte> typesection, ushort worksetCount)
        {
            static Worklet PopWorklet(Worklet[] workset, ref ushort worksetPointer) => workset[worksetPointer++];
            static void PushWorklet(Worklet[] workset, ref ushort worksetTop, Worklet worklet) => workset[worksetTop++] = worklet;

            short[] recordedStackHeight = ArrayPool<short>.Shared.Rent(code.Length);
            ushort suggestedMaxHeight = typesection.Slice(sectionId * MINIMUM_TYPESECTION_SIZE + TWO_BYTE_LENGTH, TWO_BYTE_LENGTH).ReadEthUInt16();

            ushort curr_outputs = typesection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET] == 0x80 ? (ushort)0 : typesection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
            ushort peakStackHeight = typesection[sectionId * MINIMUM_TYPESECTION_SIZE + INPUTS_OFFSET];

            ushort worksetTop = 0; ushort worksetPointer = 0;
            Worklet[] workset = ArrayPool<Worklet>.Shared.Rent(worksetCount + 1);

            int unreachedBytes = code.Length;

            try
            {
                PushWorklet(workset, ref worksetTop, new Worklet(0, peakStackHeight));
                while (worksetPointer < worksetTop)
                {
                    Worklet worklet = PopWorklet(workset, ref worksetPointer);

                    while (worklet.Position < code.Length)
                    {
                        Instruction opcode = (Instruction)code[worklet.Position];
                        (ushort? inputs, ushort? outputs, ushort? immediates) = opcode.StackRequirements();
                        ushort posPostInstruction = (ushort)(worklet.Position + 1);
                        if (recordedStackHeight[worklet.Position] != 0)
                        {
                            if (worklet.StackHeight != recordedStackHeight[worklet.Position] - 1)
                            {
                                if (Logger.IsTrace) Logger.Trace($"EIP-5450 : Branch joint line has invalid stack height");
                                return false;
                            }
                            break;
                        }
                        else
                        {
                            recordedStackHeight[worklet.Position] = (short)(worklet.StackHeight + 1);
                            unreachedBytes -= ONE_BYTE_LENGTH;
                        }

                        switch (opcode)
                        {
                            case Instruction.CALLF or Instruction.JUMPF:
                                ushort sectionIndex = code.Slice(posPostInstruction, immediates.Value).ReadEthUInt16();
                                inputs = typesection[sectionIndex * MINIMUM_TYPESECTION_SIZE + INPUTS_OFFSET];

                                outputs = typesection[sectionIndex * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
                                outputs = (ushort)(outputs == 0x80 ? 0 : outputs);

                                ushort maxStackHeight = typesection.Slice(sectionIndex * MINIMUM_TYPESECTION_SIZE + MAX_STACK_HEIGHT_OFFSET, TWO_BYTE_LENGTH).ReadEthUInt16();
                                unreachedBytes -= immediates.Value;

                                if (worklet.StackHeight + maxStackHeight - inputs > MAX_STACK_HEIGHT)
                                {
                                    if (Logger.IsTrace) Logger.Trace($"EIP-5450 : stack head during callf must not exceed {MAX_STACK_HEIGHT}");
                                    return false;
                                }
                                break;
                            case Instruction.DUPN:
                                byte imm = code[posPostInstruction];
                                inputs = (ushort)(imm + 1);
                                outputs = (ushort)(inputs + 1);
                                unreachedBytes -= immediates.Value;
                                break;
                            case Instruction.SWAPN:
                                imm = code[posPostInstruction];
                                outputs = inputs = (ushort)(1 + imm);
                                unreachedBytes -= immediates.Value;
                                break;
                            case Instruction.EXCHANGE:
                                byte imm_n = (byte)(code[posPostInstruction] >> 4);
                                byte imm_m = (byte)(code[posPostInstruction] & 0x0F);
                                outputs = inputs = (ushort)(imm_n + imm_m);
                                unreachedBytes -= immediates.Value;
                                break;
                        }

                        if (worklet.StackHeight < inputs)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-5450 : Stack Underflow required {inputs} but found {worklet.StackHeight}");
                            return false;
                        }

                        worklet.StackHeight += (ushort)(outputs - inputs + (opcode is Instruction.JUMPF ? curr_outputs : 0));
                        peakStackHeight = Math.Max(peakStackHeight, worklet.StackHeight);

                        switch (opcode)
                        {
                            case Instruction.JUMPF:
                                {
                                    if (curr_outputs < outputs)
                                    {
                                        if (Logger.IsTrace) Logger.Trace($"EIP-6206 : Output Count {outputs} must be less or equal than sectionId {sectionId} output count {curr_outputs}");
                                        return false;
                                    }

                                    if (worklet.StackHeight != curr_outputs + inputs - outputs)
                                    {
                                        if (Logger.IsTrace) Logger.Trace($"EIP-6206 : Stack Height must {curr_outputs + inputs - outputs} but found {worklet.StackHeight}");
                                        return false;
                                    }

                                    break;
                                }
                            case Instruction.RJUMP or Instruction.RJUMPI:
                                {
                                    short offset = code.Slice(posPostInstruction, immediates.Value).ReadEthInt16();
                                    int jumpDestination = posPostInstruction + immediates.Value + offset;
                                    PushWorklet(workset, ref worksetTop, new Worklet((ushort)jumpDestination, worklet.StackHeight));
                                    posPostInstruction += immediates.Value;
                                    unreachedBytes -= immediates.Value;
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
                                        PushWorklet(workset, ref worksetTop, new Worklet((ushort)jumpDestination, worklet.StackHeight));
                                    }
                                    posPostInstruction += immediates.Value;
                                    unreachedBytes -= immediates.Value;
                                    break;
                                }
                            default:
                                {
                                    unreachedBytes -= immediates.Value;
                                    posPostInstruction += immediates.Value;
                                    break;
                                }
                        }

                        worklet.Position = posPostInstruction;

                        if (opcode.IsTerminating())
                        {
                            if(opcode is Instruction.RJUMP)
                            {
                                break;
                            }

                            var expectedHeight = opcode is Instruction.RETF ? typesection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET] : worklet.StackHeight;
                            if (expectedHeight != worklet.StackHeight)
                            {
                                if (Logger.IsTrace) Logger.Trace($"EIP-5450 : Stack state invalid required height {expectedHeight} but found {worklet.StackHeight}");
                                return false;
                            }
                            break;
                        }

                        else if (worklet.Position >= code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-5450 : Invalid code, reached end of code without a terminating instruction");
                            return false;
                        }
                    }
                }

                if (unreachedBytes > 0)
                {
                    if (Logger.IsTrace) Logger.Trace($"EIP-5450 : bytecode has unreachable segments");
                    return false;
                }

                if (peakStackHeight != suggestedMaxHeight)
                {
                    if (Logger.IsTrace) Logger.Trace($"EIP-5450 : Suggested Max Stack height mismatches with actual Max, expected {suggestedMaxHeight} but found {peakStackHeight}");
                    return false;
                }

                bool result = peakStackHeight <= MAX_STACK_HEIGHT;
                if (!result)
                {
                    if (Logger.IsTrace) Logger.Trace($"EIP-5450 : stack overflow exceeded max stack height of {MAX_STACK_HEIGHT} but found {peakStackHeight}");
                    return false;
                }
                return result;
            }
            finally
            {
                ArrayPool<short>.Shared.Return(recordedStackHeight, true);
                ArrayPool<Worklet>.Shared.Return(workset, true);
            }
        }
    }
}
