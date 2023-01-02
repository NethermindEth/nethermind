using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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
    private static readonly Dictionary<byte, IEofVersionHandler> _eofVersionHandlers = new();
    internal static ILogger Logger { get; set; } = NullLogger.Instance;

    static EvmObjectFormat()
    {
        _eofVersionHandlers.Add(0x01, new Eof1());
    }

    /// <summary>
    /// returns whether the code passed is supposed to be treated as Eof regardless of its validity.
    /// </summary>
    /// <param name="container">Machine code to be checked</param>
    /// <returns></returns>
    public static bool IsEof(ReadOnlySpan<byte> container) => container.StartsWith(MAGIC);

    public static bool IsValidEof(ReadOnlySpan<byte> container, byte version)
    {
        if (container.Length >= 7
            && container[2] == version
            && _eofVersionHandlers.TryGetValue(container[2], out IEofVersionHandler handler)
            && handler.TryParseEofHeader(container, out EofHeader? header))
        {
            EofHeader h = header.Value;
            return handler.ValidateBody(container, in h);
        }

        return false;
    }

    public static bool IsValidEof(ReadOnlySpan<byte> container, out EofHeader? header)
    {
        if (container.Length >= 7
            && _eofVersionHandlers.TryGetValue(container[2], out IEofVersionHandler handler)
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
        return container.Length >= 7
               && _eofVersionHandlers.TryGetValue(container[2], out IEofVersionHandler handler)
               && handler.TryParseEofHeader(container, out header);
    }

    private class Eof1 : IEofVersionHandler
    {
        private const byte VERSION = 0x01;
        private const byte KIND_TYPE = 0x01;
        private const byte KIND_CODE = 0x02;
        private const byte KIND_DATA = 0x03;
        private const byte TERMINATOR = 0x00;

        private const byte VERSION_SIZE = 1;
        private const byte SECTION_SIZE = 3;
        private const byte TERMINATOR_SIZE = 1;

        private const byte MINIMUM_TYPESECTION_SIZE = 4;
        private const byte MINIMUM_CODESECTION_SIZE = 1;

        private const ushort MINIMUM_CODESECTIONS_COUNT = 1;
        private const ushort MAXIMUM_CODESECTIONS_COUNT = 1024;

        private const byte IMMEDIATE_16BIT_BYTE_COUNT = 2;
        public static int MINIMUM_HEADER_SIZE => CalculateHeaderSize(1);

        public static int CalculateHeaderSize(int numberOfSections) =>
            MAGIC.Length
            + VERSION_SIZE
            + SECTION_SIZE // type
            + GetArraySectionSize(numberOfSections) // code
            + SECTION_SIZE // data
            + TERMINATOR_SIZE;

        public static int GetArraySectionSize(int numberOfSections) => 3 + numberOfSections * 2;

        private bool CheckBounds(int index, int length, ref EofHeader? header)
        {
            if (index >= length)
            {
                header = null;
                return false;
            }
            return true;
        }

        public bool TryParseEofHeader(ReadOnlySpan<byte> container, out EofHeader? header)
        {
            header = null;

            if (!container.StartsWith(MAGIC))
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Code doesn't start with Magic byte sequence expected {MAGIC.ToHexString(true)} ");
                return false;
            }

            if (container[MAGIC.Length] != VERSION)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Code is not Eof version {VERSION}");
                return false;
            }

            if (container.Length < MINIMUM_HEADER_SIZE
                + MINIMUM_TYPESECTION_SIZE // minimum type section body size
                + MINIMUM_CODESECTION_SIZE) // minimum code section body size
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Eof{VERSION}, Code is too small to be valid code");
                return false;
            }


            ushort numberOfCodeSections = container[7..9].ReadEthUInt16();
            if (numberOfCodeSections < MINIMUM_CODESECTIONS_COUNT)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : At least one code section must be present");
                return false;
            }

            if (numberOfCodeSections > MAXIMUM_CODESECTIONS_COUNT)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : code sections count must not exceed 1024");
                return false;
            }

            int headerSize = CalculateHeaderSize(numberOfCodeSections);
            int pos = 3;

            if (container[pos] != KIND_TYPE)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
                return false;
            }

            pos++;
            if (!CheckBounds(pos + IMMEDIATE_16BIT_BYTE_COUNT, container.Length, ref header))
            {
                return false;
            }

            SectionHeader typeSection = new()
            {
                Start = headerSize,
                Size = container[pos..(pos + IMMEDIATE_16BIT_BYTE_COUNT)].ReadEthUInt16()
            };

            if (typeSection.Size < MINIMUM_TYPESECTION_SIZE)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : TypeSection Size must be at least 3, but found {typeSection.Size}");
                return false;
            }

            pos += IMMEDIATE_16BIT_BYTE_COUNT;

            if (container[pos] != KIND_CODE)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
                return false;
            }

            pos += 3; // kind_code(1) + num_code_sections(2)
            if (!CheckBounds(pos, container.Length, ref header))
            {
                return false;
            }

            SectionHeader[] codeSections = new SectionHeader[numberOfCodeSections];
            int lastEndOffset = typeSection.EndOffset;
            int codeSectionsSize = 0;
            for (ushort i = 0; i < numberOfCodeSections; i++)
            {
                if (!CheckBounds(pos + IMMEDIATE_16BIT_BYTE_COUNT, container.Length, ref header))
                {
                    return false;
                }

                SectionHeader codeSection = new()
                {
                    Start = lastEndOffset,
                    Size = container[pos..(pos + IMMEDIATE_16BIT_BYTE_COUNT)].ReadEthUInt16()
                };

                if (codeSection.Size == 0)
                {
                    if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Empty Code Section are not allowed, CodeSectionSize must be > 0 but found {codeSection.Size}");
                    return false;
                }

                codeSections[i] = codeSection;
                lastEndOffset = codeSection.EndOffset;
                codeSectionsSize += codeSection.Size;
                pos += IMMEDIATE_16BIT_BYTE_COUNT;

            }

            if (container[pos] != KIND_DATA)
            {
                if (Logger.IsTrace) Logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
                return false;
            }

            pos++;
            if (!CheckBounds(pos + IMMEDIATE_16BIT_BYTE_COUNT, container.Length, ref header))
            {
                return false;
            }

            SectionHeader dataSection = new()
            {
                Start = lastEndOffset,
                Size = container[(pos)..(pos + IMMEDIATE_16BIT_BYTE_COUNT)].ReadEthUInt16()
            };

            pos += IMMEDIATE_16BIT_BYTE_COUNT;
            if (container[pos] != TERMINATOR)
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

            // if (contractBody[INPUTS_OFFSET] > INPUTS_MAX)
            // {
            //     if (Logger.IsTrace) Logger.Trace("EIP-3540 : Too many inputs");
            //     return false;
            // }
            //
            // if (contractBody[OUTPUTS_OFFSET] > OUTPUTS_MAX)
            // {
            //     if (Logger.IsTrace) Logger.Trace("EIP-3540 : Too many outputs");
            //     return false;
            // }
            //
            // ushort maxStackHeight = contractBody.Slice(MAX_STACK_HEIGHT_OFFSET, MAX_STACK_HEIGHT_LENGTH).ReadEthUInt16();
            // if (maxStackHeight > MAX_STACK_HEIGHT)
            // {
            //     if (Logger.IsTrace) Logger.Trace("EIP-3540 : Stack depth too high");
            //     return false;
            // }

            return true;
        }
    }
}
