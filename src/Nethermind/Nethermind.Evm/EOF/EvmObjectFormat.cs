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
    private const byte ONE_BYTE_LENGTH = 1;
    private const byte TWO_BYTE_LENGTH = 2;
    private const byte VERSION_OFFSET = TWO_BYTE_LENGTH; // magic lenght

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
    public static bool IsEof(ReadOnlySpan<byte> container) => container.StartsWith(MAGIC);
    public static bool IsEofn(ReadOnlySpan<byte> container, byte version) => container.Length >= MAGIC.Length + 1 && container.StartsWith(MAGIC) && container[MAGIC.Length] == version;

    public static bool IsValidEof(ReadOnlySpan<byte> container, out EofHeader? header)
    {
        if (container.Length > VERSION_OFFSET
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

            if (container[KIND_TYPE_OFFSET] != KIND_TYPE)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
                return false;
            }

            if (container[KIND_CODE_OFFSET] != KIND_CODE)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
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
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : TypeSection Size must be at least 3, but found {typeSection.Size}");
                return false;
            }

            if (numberOfCodeSections < MINIMUM_NUM_CODE_SECTIONS)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : At least one code section must be present");
                return false;
            }

            if (numberOfCodeSections > MAXIMUM_NUM_CODE_SECTIONS)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : code sections count must not exceed 1024");
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
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Eof{VERSION}, Code is too small to be valid code");
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
                    if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Empty Code Section are not allowed, CodeSectionSize must be > 0 but found {codeSection.Size}");
                    return false;
                }

                codeSections[pos] = codeSection;
                codeSectionsSizeUpToNow += codeSection.Size;
            }

            if (container[KIND_DATA_OFFSET + dynamicOffset] != KIND_DATA)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
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
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
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
                if (Logger.IsTrace) Logger.Trace("EIP-3540 : SectionSizes indicated in bundled header are incorrect, or ContainerCode is incomplete");
                return false;
            }

            if (codeSections.Length == 0 || codeSections.Any(section => section.Size == 0))
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : CodeSection size must follow a CodeSection, CodeSection length was {codeSections.Length}");
                return false;
            }

            if (codeSections.Length != (typeSectionSize / MINIMUM_TYPESECTION_SIZE))
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-4750: Code Sections count must match TypeSection count, CodeSection count was {codeSections.Length}, expected {typeSectionSize / MINIMUM_TYPESECTION_SIZE}");
                return false;
            }

            ReadOnlySpan<byte> typesection = container.Slice(typeSectionStart, typeSectionSize);
            if (!ValidateTypeSection(typesection))
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-4750: invalid typesection found");
                return false;
            }

            bool[] visitedSections = ArrayPool<bool>.Shared.Rent(header.CodeSections.Length);
            Queue<ushort> validationQueue = new Queue<ushort>();
            validationQueue.Enqueue(0);

            while (validationQueue.TryDequeue(out ushort sectionIdx))
            {
                if (visitedSections[sectionIdx])
                {
                    continue;
                }

                visitedSections[sectionIdx] = true;
                SectionHeader sectionHeader = header.CodeSections[sectionIdx];
                (int codeSectionStartOffset, int codeSectionSize) = sectionHeader;
                ReadOnlySpan<byte> code = container.Slice(codeSectionStartOffset, codeSectionSize);
                if (!ValidateInstructions(sectionIdx, typesection ,code, header, validationQueue, out ushort jumpsCount))
                {
                    return false;
                }
            }

            return visitedSections[..header.CodeSections.Length].All(id => id);
        }

        bool ValidateTypeSection(ReadOnlySpan<byte> types)
        {
            if (types[SECTION_INPUT_COUNT_OFFSET] != 0 || types[SECTION_OUTPUT_COUNT_OFFSET] != 0)
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
                byte inputCount = types[offset + SECTION_INPUT_COUNT_OFFSET];
                byte outputCount = types[offset + SECTION_OUTPUT_COUNT_OFFSET];
                ushort maxStackHeight = types.Slice(offset + MAX_STACK_HEIGHT_OFFSET, MAX_STACK_HEIGHT_LENGTH).ReadEthUInt16();

                if (inputCount > INPUTS_MAX)
                {
                    if (Logger.IsTrace) Logger.Trace("EIP-3540 : Too many inputs");
                    return false;
                }

                if (outputCount > OUTPUTS_MAX)
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
        bool ValidateInstructions(ushort sectionId, ReadOnlySpan<byte> typesection, ReadOnlySpan<byte> code, in EofHeader header, Queue<ushort> worklist, out ushort jumpsCount)
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

                        BitmapHelper.HandleNumbits(TWO_BYTE_LENGTH, codeBitmap, ref postInstructionByte);
                        if (targetSectionId >= header.CodeSectionsSize)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-6206 : JUMPF to unknown code section");
                            return false;
                        }
                    }

                    if (opcode is Instruction.RJUMPV)
                    {
                        if (postInstructionByte + TWO_BYTE_LENGTH > code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-4200 : Static Relative Jumpv Argument underflow");
                            return false;
                        }

                        byte count = code[postInstructionByte];
                        jumpsCount += count;
                        if (count < MINIMUMS_ACCEPTABLE_JUMPT_JUMPTABLE_LENGTH)
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
                        BitmapHelper.HandleNumbits(TWO_BYTE_LENGTH, codeBitmap, ref postInstructionByte);

                        if (targetSectionId >= header.CodeSections.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EIP-4750 : Invalid Section Id");
                            return false;
                        }

                        worklist.Enqueue(targetSectionId);
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
        public bool ValidateReachableCode(in ReadOnlySpan<byte> code, short[] reachedOpcode)
        {
            for (int pos = 0; pos < code.Length;)
            {
                var opcode = (Instruction)code[pos];

                if (reachedOpcode[pos] == 0)
                {
                    return false;
                }

                pos++;
                if (opcode is Instruction.RJUMP or Instruction.RJUMPI or Instruction.CALLF or Instruction.JUMPF)
                {
                    pos += TWO_BYTE_LENGTH;
                }
                else if (opcode is Instruction.RJUMPV)
                {
                    byte count = code[pos];

                    pos += ONE_BYTE_LENGTH + count * TWO_BYTE_LENGTH;
                }
                else if (opcode is >= Instruction.PUSH0 and <= Instruction.PUSH32)
                {
                    int len = opcode - Instruction.PUSH0;
                    pos += len;
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
            int curr_outputs = typesection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
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
                                if (Logger.IsTrace) Logger.Trace($"EIP-5450 : Branch joint line has invalid stack height");
                                return false;
                            }
                            break;
                        }
                        else
                        {
                            recordedStackHeight[worklet.Position] = (short)(worklet.StackHeight + 1);
                        }

                        if (opcode is Instruction.CALLF or Instruction.JUMPF)
                        {
                            ushort sectionIndex = code.Slice(posPostInstruction, TWO_BYTE_LENGTH).ReadEthUInt16();
                            inputs = typesection[sectionIndex * MINIMUM_TYPESECTION_SIZE + INPUTS_OFFSET];
                            outputs = typesection[sectionIndex * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
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
                            var expectedHeight = opcode switch
                            {
                                Instruction.RETF => typesection[sectionId * MINIMUM_TYPESECTION_SIZE + 1],
                                _ => worklet.StackHeight
                            };

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

                if (!ValidateReachableCode(code, recordedStackHeight))
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
