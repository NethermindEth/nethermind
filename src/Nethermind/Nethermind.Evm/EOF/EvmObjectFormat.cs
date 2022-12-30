using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Logging;

namespace Nethermind.Evm.EOF;

public interface IEofVersionHandler
{
    bool ValidateCode(ReadOnlySpan<byte> code, out EofHeader? header);
    bool TryParseEofHeader(ReadOnlySpan<byte> code, out EofHeader? header);
}

public class EvmObjectFormat
{
    // magic prefix : EofFormatByte is the first byte, EofFormatDiff is chosen to diff from previously rejected contract according to EIP3541
    private static byte[] EOF_MAGIC = { 0xEF, 0x00 };

    private readonly Dictionary<byte, IEofVersionHandler> _eofVersionHandlers = new();

    private readonly ILogger _logger;
    private bool LoggingEnabled => _logger?.IsTrace ?? false;
    public EvmObjectFormat(ILogManager logManager = null)
    {
        _logger = logManager?.GetClassLogger<EvmObjectFormat>();
        _eofVersionHandlers.Add(0x01, new Eof1(logManager));
    }

    /// <summary>
    /// returns whether the code passed is supposed to be treated as Eof regardless of its validity.
    /// </summary>
    /// <param name="container">Machine code to be checked</param>
    /// <returns></returns>
    public bool IsEof(ReadOnlySpan<byte> container) => container.StartsWith(EOF_MAGIC);

    public bool IsValidEof(ReadOnlySpan<byte> container, out EofHeader? header)
    {
        header = null;
        if (container.Length < 7)
            return false;
        return _eofVersionHandlers.ContainsKey(container[2])
            ? _eofVersionHandlers[container[2]].ValidateCode(container, out header) // will handle rest of validations
            : false; // will handle version == 0;
    }

    public bool TryExtractEofHeader(ReadOnlySpan<byte> container, [NotNullWhen(true)] out EofHeader? header)
    {
        header = null;
        if (container.Length < 7 || !_eofVersionHandlers.ContainsKey(container[2]))
            return false; // log
        if (!_eofVersionHandlers[container[2]].TryParseEofHeader(container, out header))
            return false; // log
        return true;
    }


    public class Eof1 : IEofVersionHandler
    {
        private const byte VERSION = 0x01;

        private const byte KIND_TYPE = 0x01;
        private const byte KIND_CODE = 0x02;
        private const byte KIND_DATA = 0x03;
        private const byte TERMINATOR = 0x00;

        private const byte VERSION_SIZE = 1;
        private const byte SECTION_SIZE = 3;
        private const byte TERMINATOR_SIZE = 1;

        private const byte MINIMUM_TYPESECTION_SIZE= 4;
        private const byte MINIMUM_CODESECTION_SIZE = 1;

        private const ushort MINIMUM_CODESECTIONS_COUNT = 1;
        private const ushort MAXIMUM_CODESECTIONS_COUNT = 1024;

        private const byte IMMEDIATE_16BIT_BYTE_COUNT = 2;
        public static int MINIMUM_HEADER_SIZE => CalculateHeaderSize(1);
        public static int CalculateHeaderSize(int numberOfSections) => EOF_MAGIC.Length + VERSION_SIZE
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

        private readonly ILogger? _logger;
        private bool _loggerEnabled => _logger?.IsTrace ?? false;

        public Eof1(ILogManager? logManager = null)
        {
            _logger = logManager?.GetClassLogger<Eof1>();
        }

        public bool ValidateCode(ReadOnlySpan<byte> container, out EofHeader? header)
        {
            return TryParseEofHeader(container, out header) && ValidateBody(container, ref header);
        }

        public bool TryParseEofHeader(ReadOnlySpan<byte> container, [NotNullWhen(true)] out EofHeader? header)
        {
            header = null;
            if (!container.StartsWith(EOF_MAGIC))
            {
                if (_loggerEnabled)
                    _logger.Trace($"EIP-3540 : Code doesn't start with Magic byte sequence expected {EOF_MAGIC.ToHexString(true)} ");
                return false;
            }
            if (container[EOF_MAGIC.Length] != VERSION)
            {
                if (_loggerEnabled)
                    _logger.Trace($"EIP-3540 : Code is not Eof version {VERSION}");
                return false;
            }

            if (container.Length < MINIMUM_HEADER_SIZE
                + MINIMUM_TYPESECTION_SIZE // minimum type section body size
                + MINIMUM_CODESECTION_SIZE) // minimum code section body size
            {
                if (_loggerEnabled)
                    _logger.Trace($"EIP-3540 : Eof{VERSION}, Code is too small to be valid code");
                return false;
            }


            ushort numberOfCodeSections = container[7..9].ReadEthUInt16();
            if (numberOfCodeSections < MINIMUM_CODESECTIONS_COUNT)
            {
                if (_loggerEnabled)
                    _logger.Trace($"EIP-3540 : At least one code section must be present");
                return false;
            }

            if (numberOfCodeSections > MAXIMUM_CODESECTIONS_COUNT)
            {
                if (_loggerEnabled)
                    _logger.Trace($"EIP-3540 : code sections count must not exceed 1024");
                return false;
            }

            int headerSize = CalculateHeaderSize(numberOfCodeSections);
            int pos = 3;

            if (container[pos] != KIND_TYPE)
            {
                if (_loggerEnabled)
                    _logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
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
                if (_loggerEnabled)
                    _logger.Trace($"EIP-3540 : TypeSection Size must be at least 4, but found {typeSection.Size}");
                return false;
            }

            pos += IMMEDIATE_16BIT_BYTE_COUNT;

            if (container[pos] != KIND_CODE)
            {
                if (_loggerEnabled)
                    _logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
                return false;
            }

            pos += 3; // kind_code(1) + num_code_sections(2)
            if (!CheckBounds(pos, container.Length, ref header))
            {
                return false;
            }

            List<SectionHeader> codeSections = new();
            int lastEndOffset = typeSection.EndOffset;
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
                    if (_loggerEnabled)
                        _logger.Trace($"EIP-3540 : Empty Code Section are not allowed, CodeSectionSize must be > 0 but found {codeSection.Size}");
                    return false;
                }

                codeSections.Add(codeSection);
                lastEndOffset = codeSection.EndOffset;
                pos += IMMEDIATE_16BIT_BYTE_COUNT;

            }


            if (container[pos] != KIND_DATA)
            {
                if (_loggerEnabled)
                    _logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
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
                if (_loggerEnabled)
                    _logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
                return false;
            }

            header = new EofHeader
            {
                Version = VERSION,
                TypeSection = typeSection,
                CodeSections = codeSections.ToArray(),
                DataSection = dataSection
            };
            return true;
        }

        bool ValidateBody(ReadOnlySpan<byte> container, ref EofHeader? header)
        {
            int startOffset = CalculateHeaderSize(header.Value.CodeSections.Length);
            int calculatedCodeLength = header.Value.TypeSection.Size
                + header.Value.CodeSections.Sum(c => c.Size)
                + header.Value.DataSection.Size;

            ReadOnlySpan<byte> contractBody = container[startOffset..];

            if (contractBody.Length != calculatedCodeLength)
            {
                header = null;
                if (_loggerEnabled)
                    _logger.Trace($"EIP-3540 : SectionSizes indicated in bundeled header are incorrect, or ContainerCode is incomplete");
                return false;
            }
            return true;
        }
    }
}
