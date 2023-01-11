using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
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
        if (container.Length > VERSION_OFFSET
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

    private class Eof1 : IEofVersionHandler
    {
        public const byte VERSION = 0x01;
        private const byte KIND_TYPE = 0x01;
        private const byte KIND_CODE = 0x02;
        private const byte KIND_DATA = 0x03;
        private const byte TERMINATOR = 0x00;

        private const byte MINIMUM_TYPESECTION_SIZE = 4;
        private const byte MINIMUM_CODESECTION_SIZE = 1;

        private const byte KIND_TYPE_OFFSET = VERSION_OFFSET + ONE_BYTE_LENGTH; // version length
        private const byte TYPE_SIZE_OFFSET = KIND_TYPE_OFFSET + ONE_BYTE_LENGTH; // kind type length
        private const byte KIND_CODE_OFFSET = TYPE_SIZE_OFFSET + TWO_BYTE_LENGTH; // type size length
        private const byte NUM_CODE_SECTIONS_OFFSET = KIND_CODE_OFFSET + ONE_BYTE_LENGTH; // kind code length
        private const byte CODESIZE_OFFSET = NUM_CODE_SECTIONS_OFFSET + TWO_BYTE_LENGTH; // num code sections length
        private const byte KIND_DATA_OFFSET = CODESIZE_OFFSET + DYNAMIC_OFFSET; // all code size length
        private const byte DATA_SIZE_OFFSET = KIND_DATA_OFFSET + ONE_BYTE_LENGTH + DYNAMIC_OFFSET; // kind data length + all code size length
        private const byte TERMINATOR_OFFSET = DATA_SIZE_OFFSET + TWO_BYTE_LENGTH + DYNAMIC_OFFSET; // data size length + all code size length
        private const byte DYNAMIC_OFFSET = 0; // to mark dynamic offset needs to be added

        private const ushort MINIMUM_NUM_CODE_SECTIONS = 1;
        private const ushort MAXIMUM_NUM_CODE_SECTIONS = 1024;

        private const ushort MINIMUM_SIZE = TERMINATOR_OFFSET
                                           + TWO_BYTE_LENGTH // one code size
                                           + MINIMUM_TYPESECTION_SIZE // minimum type section body size
                                           + MINIMUM_CODESECTION_SIZE; // minimum code section body size;


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CalculateHeaderSize(int codeSections) =>
            TERMINATOR_OFFSET + ONE_BYTE_LENGTH + codeSections * TWO_BYTE_LENGTH;

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

            int codeSizeLenght = numberOfCodeSections * TWO_BYTE_LENGTH;
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

            int codeSectionsSize = 0;
            SectionHeader[] codeSections = new SectionHeader[numberOfCodeSections];
            for (ushort i = 0; i < numberOfCodeSections; i++)
            {
                int currentCodeSizeOffset = CODESIZE_OFFSET + i * TWO_BYTE_LENGTH; // offset of i'th code size
                SectionHeader codeSection = new()
                {
                    Start = currentCodeSizeOffset,
                    Size = GetUInt16(container, currentCodeSizeOffset)
                };

                if (codeSection.Size == 0)
                {
                    if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Empty Code Section are not allowed, CodeSectionSize must be > 0 but found {codeSection.Size}");
                    return false;
                }

                codeSections[i] = codeSection;
                codeSectionsSize += codeSection.Size;
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
                CodeSectionsSize = codeSectionsSize,
                DataSection = dataSection
            };

            return true;
        }

        private const byte INPUTS_OFFSET = 0;
        private const byte INPUTS_MAX = 0x7F;
        private const byte OUTPUTS_OFFSET = INPUTS_OFFSET + 1;
        private const byte OUTPUTS_MAX = 0x7F;
        private const byte MAX_STACK_HEIGHT_OFFSET = OUTPUTS_OFFSET + 1;
        private const int MAX_STACK_HEIGHT_LENGTH = 2;
        private const ushort MAX_STACK_HEIGHT = 0x3FF;

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

            return true;
        }
    }
}
