// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;

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
    private static byte[] MAGIC = { 0xEF, 0x00 };
    private const byte ONE_BYTE_LENGTH = 1;
    private const byte TWO_BYTE_LENGTH = 2;
    private const byte VERSION_OFFSET = TWO_BYTE_LENGTH; // magic lenght

    private static readonly Dictionary<byte, IEofVersionHandler> _eofVersionHandlers = new();
    static EvmObjectFormat()
    {
        _eofVersionHandlers.Add(Eof1.VERSION, new Eof1());
    }

    /// <summary>
    /// returns whether the code passed is supposed to be treated as Eof regardless of its validity.
    /// </summary>
    /// <param name="container">Machine code to be checked</param>
    /// <returns></returns>
    public static bool IsEof(ReadOnlySpan<byte> container) => container.StartsWith(MAGIC);

    public static bool IsValidEof(ReadOnlySpan<byte> container, out EofHeader? header)
    {
        if (container.Length >= VERSION_OFFSET
            && _eofVersionHandlers.TryGetValue(container[VERSION_OFFSET], out IEofVersionHandler handler)
            && handler.TryParseEofHeader(container, out header))
        {
            EofHeader h = header.Value;
            if (handler.ValidateBody(container, h))
            {
                return true;
            }
        }

        header = null;
        return false;
    }

    public static bool TryExtractHeader(ReadOnlySpan<byte> container, [NotNullWhen(true)] out EofHeader? header)
    {
        header = null;
        return container.Length >= VERSION_OFFSET
               && _eofVersionHandlers.TryGetValue(container[VERSION_OFFSET], out IEofVersionHandler handler)
               && handler.TryParseEofHeader(container, out header);
    }

    public static byte GetCodeVersion(ReadOnlySpan<byte> container)
    {
        return container.Length < VERSION_OFFSET
            ? byte.MinValue
            : container[VERSION_OFFSET];
    }

    internal class Eof1 : IEofVersionHandler
    {
        public const byte VERSION = 0x01;
        internal const byte KIND_TYPE = 0x01;
        internal const byte KIND_CODE = 0x02;
        internal const byte KIND_DATA = 0x03;
        internal const byte TERMINATOR = 0x00;

        internal const byte MINIMUM_TYPESECTION_SIZE = 4;
        internal const byte MINIMUM_CODESECTION_SIZE = 1;

        internal const byte KIND_TYPE_OFFSET = VERSION_OFFSET + EvmObjectFormat.ONE_BYTE_LENGTH; // version length
        internal const byte TYPE_SIZE_OFFSET = KIND_TYPE_OFFSET + EvmObjectFormat.ONE_BYTE_LENGTH; // kind type length
        internal const byte KIND_CODE_OFFSET = TYPE_SIZE_OFFSET + EvmObjectFormat.TWO_BYTE_LENGTH; // type size length
        internal const byte NUM_CODE_SECTIONS_OFFSET = KIND_CODE_OFFSET + EvmObjectFormat.ONE_BYTE_LENGTH; // kind code length
        internal const byte CODESIZE_OFFSET = NUM_CODE_SECTIONS_OFFSET + EvmObjectFormat.TWO_BYTE_LENGTH; // num code sections length
        internal const byte KIND_DATA_OFFSET = CODESIZE_OFFSET + DYNAMIC_OFFSET; // all code size length
        internal const byte DATA_SIZE_OFFSET = KIND_DATA_OFFSET + EvmObjectFormat.ONE_BYTE_LENGTH + DYNAMIC_OFFSET; // kind data length + all code size length
        internal const byte TERMINATOR_OFFSET = DATA_SIZE_OFFSET + EvmObjectFormat.TWO_BYTE_LENGTH + DYNAMIC_OFFSET; // data size length + all code size length
        internal const byte HEADER_END_OFFSET = TERMINATOR_OFFSET + EvmObjectFormat.ONE_BYTE_LENGTH + DYNAMIC_OFFSET; // terminator length + all code size length
        internal const byte DYNAMIC_OFFSET = 0; // to mark dynamic offset needs to be added
        internal const byte TWO_BYTE_LENGTH = 2;// indicates the number of bytes to skip for immediates
        internal const byte ONE_BYTE_LENGTH = 1; // indicates the length of the count immediate of jumpv
        internal const byte BYTE_BIT_COUNT = 8; // indicates the length of the count immediate of jumpv
        internal const byte MINIMUMS_ACCEPTABLE_JUMPT_JUMPTABLE_LENGTH = 1; // indicates the length of the count immediate of jumpv

        internal const byte SECTION_INPUT_COUNT_OFFSET = 0; // to mark dynamic offset needs to be added
        internal const byte SECTION_OUTPUT_COUNT_OFFSET = 1; // to mark dynamic offset needs to be added

        internal const byte INPUTS_OFFSET = 0;
        internal const byte INPUTS_MAX = 0x7F;
        internal const byte OUTPUTS_OFFSET = INPUTS_OFFSET + 1;
        internal const byte OUTPUTS_MAX = 0x7F;
        internal const byte MAX_STACK_HEIGHT_OFFSET = OUTPUTS_OFFSET + 1;
        internal const int MAX_STACK_HEIGHT_LENGTH = 2;
        internal const ushort MAX_STACK_HEIGHT = 0x3FF;

        internal const ushort MINIMUM_NUM_CODE_SECTIONS = 1;
        internal const ushort MAXIMUM_NUM_CODE_SECTIONS = 1024;
        internal const ushort RETURN_STACK_MAX_HEIGHT = MAXIMUM_NUM_CODE_SECTIONS; // the size in the type sectionn allocated to each function section

        internal const ushort MINIMUM_SIZE = HEADER_END_OFFSET
                                            + EvmObjectFormat.TWO_BYTE_LENGTH // one code size
                                            + MINIMUM_TYPESECTION_SIZE // minimum type section body size
                                            + MINIMUM_CODESECTION_SIZE; // minimum code section body size;


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CalculateHeaderSize(int codeSections) =>
            HEADER_END_OFFSET + codeSections * EvmObjectFormat.TWO_BYTE_LENGTH;

        public bool TryParseEofHeader(ReadOnlySpan<byte> container, out EofHeader? header)
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ushort GetUInt16(ReadOnlySpan<byte> container, int offset) =>
                container.Slice(offset, TWO_BYTE_LENGTH).ReadEthUInt16();

            header = null;

            // we need to be able to parse header + minimum section lenghts
            if (container.Length < MINIMUM_SIZE)
            {
                return false;
            }

            if (!container.StartsWith(MAGIC))
            {
                return false;
            }

            if (container[VERSION_OFFSET] != VERSION)
            {
                return false;
            }

            if (container[KIND_TYPE_OFFSET] != KIND_TYPE)
            {
                return false;
            }

            if (container[KIND_CODE_OFFSET] != KIND_CODE)
            {
                return false;
            }

            ushort numberOfCodeSections = GetUInt16(container, NUM_CODE_SECTIONS_OFFSET);

            SectionHeader typeSection = new()
            {
                Start = CalculateHeaderSize(numberOfCodeSections),
                Size = GetUInt16(container, TYPE_SIZE_OFFSET)
            };

            if (typeSection.Size < MINIMUM_TYPESECTION_SIZE)
            {
                return false;
            }

            if (numberOfCodeSections < MINIMUM_NUM_CODE_SECTIONS)
            {
                return false;
            }

            if (numberOfCodeSections > MAXIMUM_NUM_CODE_SECTIONS)
            {
                return false;
            }

            int codeSizeLenght = numberOfCodeSections * EvmObjectFormat.TWO_BYTE_LENGTH;
            int dynamicOffset = codeSizeLenght;

            // we need to be able to parse header + all code sizes
            int requiredSize = TERMINATOR_OFFSET
                               + codeSizeLenght
                               + MINIMUM_TYPESECTION_SIZE // minimum type section body size
                               + MINIMUM_CODESECTION_SIZE; // minimum code section body size

            if (container.Length < requiredSize)
            {
                return false;
            }

            int codeSectionsSizeUpToNow = 0;
            SectionHeader[] codeSections = new SectionHeader[numberOfCodeSections];
            for (ushort pos = 0; pos < numberOfCodeSections; pos++)
            {
                int currentCodeSizeOffset = CODESIZE_OFFSET + pos * EvmObjectFormat.TWO_BYTE_LENGTH; // offset of pos'th code size
                SectionHeader codeSection = new()
                {
                    Start = typeSection.EndOffset + codeSectionsSizeUpToNow,
                    Size = GetUInt16(container, currentCodeSizeOffset)
                };

                if (codeSection.Size == 0)
                {
                    return false;
                }

                codeSections[pos] = codeSection;
                codeSectionsSizeUpToNow += codeSection.Size;
            }

            if (container[KIND_DATA_OFFSET + dynamicOffset] != KIND_DATA)
            {
                return false;
            }

            // do data section now to properly check length
            int dataSectionOffset = DATA_SIZE_OFFSET + dynamicOffset;
            SectionHeader dataSection = new()
            {
                Start = dataSectionOffset,
                Size = GetUInt16(container, dataSectionOffset)
            };

            if (container[TERMINATOR_OFFSET + dynamicOffset] != TERMINATOR)
            {
                return false;
            }

            header = new EofHeader
            {
                Version = VERSION,
                TypeSection = typeSection,
                CodeSections = codeSections,
                CodeSectionsSize = codeSectionsSizeUpToNow,
                DataSection = dataSection
            };

            return true;
        }

        public bool ValidateBody(ReadOnlySpan<byte> container, EofHeader header)
        {
            int startOffset = CalculateHeaderSize(header.CodeSections.Length);
            int calculatedCodeLength = header.TypeSection.Size
                + header.CodeSectionsSize
                + header.DataSection.Size;
            SectionHeader[]? codeSections = header.CodeSections;
            ReadOnlySpan<byte> contractBody = container[startOffset..];
            (int typeSectionStart, ushort typeSectionSize) = header.TypeSection;


            if (contractBody.Length != calculatedCodeLength)
            {
                return false;
            }

            if (codeSections.Length == 0 || codeSections.Any(section => section.Size == 0))
            {
                return false;
            }

            if (codeSections.Length != (typeSectionSize / MINIMUM_TYPESECTION_SIZE))
            {
                return false;
            }

            ReadOnlySpan<byte> typesection = container.Slice(typeSectionStart, typeSectionSize);
            if (!ValidateTypeSection(typesection))
            {
                return false;
            }

            for (int sectionIdx = 0; sectionIdx < header.CodeSections.Length; sectionIdx++)
            {
                SectionHeader sectionHeader = header.CodeSections[sectionIdx];
                (int codeSectionStartOffset, int codeSectionSize) = sectionHeader;
                ReadOnlySpan<byte> code = container.Slice(codeSectionStartOffset, codeSectionSize);
                if (!ValidateInstructions(code, header, out ushort jumpsCount))
                {
                    return false;
                }

                if (!ValidateStackState(sectionIdx, code, typesection, jumpsCount))
                {
                    return false;
                }
            }

            return true;
        }

        bool ValidateTypeSection(ReadOnlySpan<byte> types)
        {
            if (types[SECTION_INPUT_COUNT_OFFSET] != 0 || types[SECTION_OUTPUT_COUNT_OFFSET] != 0)
            {
                return false;
            }

            if (types.Length % MINIMUM_TYPESECTION_SIZE != 0)
            {
                return false;
            }

            for (int offset = 0; offset < types.Length; offset += MINIMUM_TYPESECTION_SIZE)
            {
                byte inputCount = types[offset + SECTION_INPUT_COUNT_OFFSET];
                byte outputCount = types[offset + SECTION_OUTPUT_COUNT_OFFSET];
                ushort maxStackHeight = types.Slice(offset + MAX_STACK_HEIGHT_OFFSET, MAX_STACK_HEIGHT_LENGTH).ReadEthUInt16();

                if (inputCount > INPUTS_MAX)
                {
                    return false;
                }

                if (outputCount > OUTPUTS_MAX)
                {
                    return false;
                }

                if (maxStackHeight > MAX_STACK_HEIGHT)
                {
                    return false;
                }
            }
            return true;
        }
        bool ValidateInstructions(ReadOnlySpan<byte> code, in EofHeader header, out ushort jumpsCount)
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
                        return false;
                    }

                    if (opcode is Instruction.RJUMP or Instruction.RJUMPI)
                    {
                        if (postInstructionByte + TWO_BYTE_LENGTH > code.Length)
                        {
                            return false;
                        }

                        var offset = code.Slice(postInstructionByte, TWO_BYTE_LENGTH).ReadEthInt16();
                        var rjumpdest = offset + TWO_BYTE_LENGTH + postInstructionByte;

                        if (rjumpdest < 0 || rjumpdest >= code.Length)
                        {
                            return false;
                        }

                        jumpsCount += opcode is Instruction.RJUMP ? ONE_BYTE_LENGTH : TWO_BYTE_LENGTH;
                        BitmapHelper.HandleNumbits(ONE_BYTE_LENGTH, jumpdests, ref rjumpdest);
                        BitmapHelper.HandleNumbits(TWO_BYTE_LENGTH, codeBitmap, ref postInstructionByte);
                    }

                    if (opcode is Instruction.RJUMPV)
                    {
                        if (postInstructionByte + TWO_BYTE_LENGTH > code.Length)
                        {
                            return false;
                        }

                        byte count = code[postInstructionByte];
                        jumpsCount += count;
                        if (count < MINIMUMS_ACCEPTABLE_JUMPT_JUMPTABLE_LENGTH)
                        {
                            return false;
                        }

                        if (postInstructionByte + ONE_BYTE_LENGTH + count * TWO_BYTE_LENGTH > code.Length)
                        {
                            return false;
                        }

                        var immediateValueSize = ONE_BYTE_LENGTH + count * TWO_BYTE_LENGTH;
                        for (int j = 0; j < count; j++)
                        {
                            var offset = code.Slice(postInstructionByte + ONE_BYTE_LENGTH + j * TWO_BYTE_LENGTH, TWO_BYTE_LENGTH).ReadEthInt16();
                            var rjumpdest = offset + immediateValueSize + postInstructionByte;
                            if (rjumpdest < 0 || rjumpdest >= code.Length)
                            {
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
                            return false;
                        }

                        ushort targetSectionId = code.Slice(postInstructionByte, TWO_BYTE_LENGTH).ReadEthUInt16();
                        BitmapHelper.HandleNumbits(TWO_BYTE_LENGTH, codeBitmap, ref postInstructionByte);

                        if (targetSectionId >= header.CodeSections.Length)
                        {
                            return false;
                        }
                    }

                    if (opcode is >= Instruction.PUSH0 and <= Instruction.PUSH32)
                    {
                        int len = opcode - Instruction.PUSH0;
                        if (postInstructionByte + len > code.Length)
                        {
                            return false;
                        }
                        BitmapHelper.HandleNumbits(len, codeBitmap, ref postInstructionByte);
                    }
                    pos = postInstructionByte;
                }

                if (pos > code.Length)
                {
                    return false;
                }

                bool result = !BitmapHelper.CheckCollision(codeBitmap, jumpdests);
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(codeBitmap, true);
                ArrayPool<byte>.Shared.Return(jumpdests, true);
            }
        }
        public bool ValidateReachableCode(in ReadOnlySpan<byte> code, short[] reachedOpcode)
        {
            for (int i = 0; i < code.Length;)
            {
                var opcode = (Instruction)code[i];

                if (reachedOpcode[i] == 0)
                {
                    return false;
                }

                i++;
                if (opcode is Instruction.RJUMP or Instruction.RJUMPI or Instruction.CALLF)
                {
                    i += TWO_BYTE_LENGTH;
                }
                else if (opcode is Instruction.RJUMPV)
                {
                    byte count = code[i];

                    i += ONE_BYTE_LENGTH + count * TWO_BYTE_LENGTH;
                }
                else if (opcode is >= Instruction.PUSH0 and <= Instruction.PUSH32)
                {
                    int len = opcode - Instruction.PUSH0;
                    i += len;
                }
            }
            return true;
        }



        public bool ValidateStackState(int sectionId, in ReadOnlySpan<byte> code, in ReadOnlySpan<byte> typesection, ushort worksetCount)
        {
            static Worklet PopWorklet(Worklet[] workset, ref ushort worksetPointer) => workset[worksetPointer++];
            static void PushWorklet(Worklet[] workset, ref ushort worksetTop, Worklet worklet) => workset[worksetTop++] = worklet;

            short[] recordedStackHeight = ArrayPool<short>.Shared.Rent(code.Length);
            ushort suggestedMaxHeight = typesection.Slice(sectionId * MINIMUM_TYPESECTION_SIZE + TWO_BYTE_LENGTH, TWO_BYTE_LENGTH).ReadEthUInt16();
            int peakStackHeight = typesection[sectionId * MINIMUM_TYPESECTION_SIZE + INPUTS_OFFSET];

            ushort worksetTop = 0; ushort worksetPointer = 0;
            Worklet[] workset = ArrayPool<Worklet>.Shared.Rent(worksetCount + 1);

            try
            {
                PushWorklet(workset, ref worksetTop, new Worklet(0, (ushort)peakStackHeight));
                while (worksetPointer < worksetTop)
                {
                    Worklet worklet = PopWorklet(workset, ref worksetPointer);
                    bool stop = false;

                    while (!stop)
                    {
                        Instruction opcode = (Instruction)code[worklet.Position];
                        (ushort inputs, ushort outputs, ushort immediates) = opcode.StackRequirements();
                        ushort posPostInstruction = (ushort)(worklet.Position + 1);
                        if (recordedStackHeight[worklet.Position] != 0)
                        {
                            if (worklet.StackHeight != recordedStackHeight[worklet.Position] - 1)
                            {
                                return false;
                            }
                            break;
                        }
                        else
                        {
                            recordedStackHeight[worklet.Position] = (short)(worklet.StackHeight + 1);
                        }

                        if (opcode is Instruction.CALLF)
                        {
                            var sectionIndex = code.Slice(posPostInstruction, TWO_BYTE_LENGTH).ReadEthUInt16();
                            inputs = typesection[sectionIndex * MINIMUM_TYPESECTION_SIZE + INPUTS_OFFSET];
                            outputs = typesection[sectionIndex * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
                        }

                        if (worklet.StackHeight < inputs)
                        {
                            return false;
                        }

                        worklet.StackHeight += (ushort)(outputs - inputs);
                        peakStackHeight = Math.Max(peakStackHeight, worklet.StackHeight);

                        switch (opcode)
                        {
                            case Instruction.RJUMP:
                                {
                                    short offset = code.Slice(posPostInstruction, TWO_BYTE_LENGTH).ReadEthInt16();
                                    int jumpDestination = posPostInstruction + immediates + offset;
                                    PushWorklet(workset, ref worksetTop, new Worklet((ushort)jumpDestination, worklet.StackHeight));
                                    stop = true;
                                    break;
                                }
                            case Instruction.RJUMPI:
                                {
                                    var offset = code.Slice(posPostInstruction, TWO_BYTE_LENGTH).ReadEthInt16();
                                    var jumpDestination = posPostInstruction + immediates + offset;
                                    PushWorklet(workset, ref worksetTop, new Worklet((ushort)jumpDestination, worklet.StackHeight));
                                    posPostInstruction += immediates;
                                    break;
                                }
                            case Instruction.RJUMPV:
                                {
                                    var count = code[posPostInstruction];
                                    immediates = (ushort)(count * TWO_BYTE_LENGTH + ONE_BYTE_LENGTH);
                                    for (short j = 0; j < count; j++)
                                    {
                                        int case_v = posPostInstruction + ONE_BYTE_LENGTH + j * TWO_BYTE_LENGTH;
                                        int offset = code.Slice(case_v, TWO_BYTE_LENGTH).ReadEthInt16();
                                        int jumpDestination = posPostInstruction + immediates + offset;
                                        PushWorklet(workset, ref worksetTop, new Worklet((ushort)jumpDestination, worklet.StackHeight));
                                    }
                                    posPostInstruction += immediates;
                                    break;
                                }
                            default:
                                {
                                    posPostInstruction += immediates;
                                    break;
                                }
                        }

                        worklet.Position = posPostInstruction;
                        if (stop) break;

                        if (opcode.IsTerminating())
                        {
                            var expectedHeight = opcode is Instruction.RETF ? typesection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET] : worklet.StackHeight;
                            if (expectedHeight != worklet.StackHeight)
                            {
                                return false;
                            }
                            break;
                        }

                        else if (worklet.Position >= code.Length)
                        {
                            return false;
                        }
                    }
                }

                if (!ValidateReachableCode(code, recordedStackHeight))
                {
                    return false;
                }

                if (peakStackHeight != suggestedMaxHeight)
                {
                    return false;
                }

                return peakStackHeight <= MAX_STACK_HEIGHT;
            }
            finally
            {
                ArrayPool<short>.Shared.Return(recordedStackHeight, true);
                ArrayPool<Worklet>.Shared.Return(workset, true);
            }
        }
    }
}
