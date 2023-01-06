// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Evm.EOF;

internal static class EvmObjectFormat
{
    private interface IEofVersionHandler
    {
        bool ValidateBody(ReadOnlySpan<byte> code, in EofHeader header);
        bool TryParseEofHeader(ReadOnlySpan<byte> code, [NotNullWhen(true)] out EofHeader? header);
    }

    // magic prefix : EofFormatByte is the first byte, EofFormatDiff is chosen to diff from previously rejected contract according to EIP3541
    private static byte[] MAGIC = { 0xEF, 0x00 };
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

    public static bool IsValidEof(ReadOnlySpan<byte> container, out EofHeader? header)
    {
        if (container.Length >= VERSION_OFFSET
            && _eofVersionHandlers.TryGetValue(container[VERSION_OFFSET], out IEofVersionHandler handler)
            && handler.TryParseEofHeader(container, out header))
        {
            EofHeader h = header.Value;
            if (handler.ValidateBody(container, in h))
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
        internal const byte MINIMUMS_ACCEPTABLE_JUMPT_JUMPTABLE_LENGTH = 1; // indicates the length of the count immediate of jumpv


        private const byte INPUTS_OFFSET = 0;
        private const byte INPUTS_MAX = 0x7F;
        private const byte OUTPUTS_OFFSET = INPUTS_OFFSET + 1;
        private const byte OUTPUTS_MAX = 0x7F;
        private const byte MAX_STACK_HEIGHT_OFFSET = OUTPUTS_OFFSET + 1;
        private const int MAX_STACK_HEIGHT_LENGTH = 2;
        private const ushort MAX_STACK_HEIGHT = 0x3FF;

        private const ushort MINIMUM_NUM_CODE_SECTIONS = 1;
        private const ushort MAXIMUM_NUM_CODE_SECTIONS = 1024;

        private const ushort MINIMUM_SIZE = HEADER_END_OFFSET
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

        public bool ValidateBody(ReadOnlySpan<byte> container, in EofHeader header)
        {
            int startOffset = CalculateHeaderSize(header.CodeSections.Length);
            int calculatedCodeLength = header.TypeSection.Size
                + header.CodeSectionsSize
                + header.DataSection.Size;

            ReadOnlySpan<byte> contractBody = container[startOffset..];

            if (contractBody.Length != calculatedCodeLength)
            {
                if (Logger.IsTrace) Logger.Trace("EIP-3540 : SectionSizes indicated in bundled header are incorrect, or ContainerCode is incomplete");
                return false;
            }

            for (int sectionIdx = 0; sectionIdx < header.CodeSections.Length; sectionIdx++)
            {
                SectionHeader sectionHeader = header.CodeSections[sectionIdx];
                (int codeSectionStartOffset, int codeSectionSize) = sectionHeader;
                ReadOnlySpan<byte> code = container.Slice(codeSectionStartOffset, codeSectionSize);
                if (!ValidateInstructions(code, header))
                {
                    if (Logger.IsTrace) Logger.Trace($"EIP-3670 : CodeSection {sectionIdx} contains invalid body");
                    return false;
                }
            }

            return true;
        }
        bool ValidateInstructions(ReadOnlySpan<byte> code, in EofHeader header)
        {
            int pos;
            BitArray immediatesMask = new BitArray(code.Length);
            BitArray rjumpdestsMask = new BitArray(code.Length);

            for (pos = 0; pos < code.Length; pos++)
            {
                Instruction opcode = (Instruction)code[pos];
                pos++;

                if (!opcode.IsValid(IsEofContext: true))
                {
                    if (Logger.IsTrace)
                    {
                        Logger.Trace($"EIP-3670 : CodeSection contains undefined opcode {opcode}");
                    }
                    return false;
                }

                if (opcode is Instruction.RJUMP or Instruction.RJUMPI)
                {
                    if (pos + TWO_BYTE_LENGTH > code.Length)
                    {
                        if (Logger.IsTrace)
                        {
                            Logger.Trace($"EIP-4200 : Static Relative Jump Argument underflow");
                        }
                        return false;
                    }

                    var offset = code.Slice(pos, TWO_BYTE_LENGTH).ReadEthInt16();
                    immediatesMask.Set(pos, true);
                    immediatesMask.Set(pos + 1, true);
                    var rjumpdest = offset + TWO_BYTE_LENGTH + pos;
                    rjumpdestsMask.Set(rjumpdest, true);

                    if (rjumpdest < 0 || rjumpdest >= code.Length)
                    {
                        if (Logger.IsTrace)
                        {
                            Logger.Trace($"EIP-4200 : Static Relative Jump Destination outside of Code bounds");
                        }
                        return false;
                    }
                    pos += TWO_BYTE_LENGTH;
                }

                if (opcode is Instruction.RJUMPV)
                {
                    if (pos + TWO_BYTE_LENGTH > code.Length)
                    {
                        if (Logger.IsTrace)
                        {
                            Logger.Trace($"EIP-4200 : Static Relative Jumpv Argument underflow");
                        }
                        return false;
                    }

                    byte count = code[pos];
                    if (count < MINIMUMS_ACCEPTABLE_JUMPT_JUMPTABLE_LENGTH)
                    {
                        if (Logger.IsTrace)
                        {
                            Logger.Trace($"EIP-4200 : jumpv jumptable must have at least 1 entry");
                        }
                        return false;
                    }

                    if (pos + ONE_BYTE_LENGTH + count * TWO_BYTE_LENGTH > code.Length)
                    {
                        if (Logger.IsTrace)
                        {
                            Logger.Trace($"EIP-4200 : jumpv jumptable underflow");
                        }
                        return false;
                    }

                    var immediateValueSize = ONE_BYTE_LENGTH + count * TWO_BYTE_LENGTH;

                    for (int j = pos; j <= pos + immediateValueSize - 1; j++)
                    {
                        immediatesMask.Set(j, true);
                    }

                    for (int j = 0; j < count; j++)
                    {
                        var offset = code.Slice(pos + ONE_BYTE_LENGTH + j * TWO_BYTE_LENGTH, TWO_BYTE_LENGTH).ReadEthInt16();
                        var rjumpdest = offset + immediateValueSize + pos;
                        rjumpdestsMask.Set(rjumpdest, true);
                        if (rjumpdest < 0 || rjumpdest >= code.Length)
                        {
                            if (Logger.IsTrace)
                            {
                                Logger.Trace($"EIP-4200 : Static Relative Jumpv Destination outside of Code bounds");
                            }
                            return false;
                        }
                    }
                    pos += immediateValueSize;
                }

                if (opcode is >= Instruction.PUSH1 and <= Instruction.PUSH32)
                {
                    int len = code[pos - 1] - (int)Instruction.PUSH1 + 1;
                    for (int j = pos; j <= pos + len - 1; j++)
                    {
                        immediatesMask.Set(j, true);
                    }
                    pos += len;
                }
            }

            if (pos >= code.Length)
            {
                if (Logger.IsTrace)
                {
                    Logger.Trace($"EIP-3670 : PC Reached out of bounds");
                }
                return false;
            }

            if (pos >= code.Length)
            {
                if (Logger.IsTrace)
                {
                    Logger.Trace($"EIP-3670 : PC Reached out of bounds");
                }
                return false;
            }


            BitArray result = rjumpdestsMask.And(immediatesMask);
            for (int i = 0; i < result.Length; i++)
            {
                if (result.Get(i))
                {
                    if (Logger.IsTrace)
                    {
                        Logger.Trace($"EIP-4200 : Static Relative Jump with invalid destination");
                    }
                    return false;
                }
            }
            return true;
        }
    }
}
