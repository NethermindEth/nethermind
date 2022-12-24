using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;

namespace Nethermind.Evm.EOF;

public interface IEofVersionHandler
{
    bool ValidateCode(ReadOnlySpan<byte> container, IReleaseSpec spec);
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

    public bool IsValidEof(ReadOnlySpan<byte> container, IReleaseSpec spec)
    {
        if (container.Length < 7)
            return false;
        return _eofVersionHandlers.ContainsKey(container[2])
            ? _eofVersionHandlers[container[2]].ValidateCode(container, spec) // will handle rest of validations
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
        private const byte TERMINATOR_SIZE = 3;
        public static int MINIMUM_HEADER_SIZE => CalculateHeaderSize(1);
        public static int CalculateHeaderSize(int numberOfSections) => EOF_MAGIC.Length + VERSION_SIZE
            + SECTION_SIZE // type
            + GetArraySectionSize(numberOfSections) // code
            + SECTION_SIZE // data
            + TERMINATOR_SIZE;
        public static int GetArraySectionSize(int numberOfSections) => 3 + numberOfSections * 2;


        private readonly ILogger? _logger;
        private bool _loggingEnabled => _logger?.IsTrace ?? false;

        public Eof1(ILogManager? logManager = null)
        {
            _logger = logManager?.GetClassLogger<Eof1>();
        }


        public bool ValidateCode(ReadOnlySpan<byte> container, IReleaseSpec spec)
        {
            return TryParseEofHeader(container, out EofHeader? header)
                && ValidateBody(container, in header)
                && (!spec.IsEip3670Enabled || ValidateInstructions(spec, container, in header));
        }

        public bool TryParseEofHeader(ReadOnlySpan<byte> container, [NotNullWhen(true)] out EofHeader? header)
        {
            header = null;
            if (!container.StartsWith(EOF_MAGIC))
            {
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : Code doesn't start with Magic byte sequence expected {EOF_MAGIC.ToHexString(true)} ");
                return false;
            }
            if (container[2] != VERSION)
            {
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : Code is not Eof version {VERSION}");
                return false;
            }
            if (container.Length < MINIMUM_HEADER_SIZE
                + 1 + 1 + 2 // minimum type section body size
                + 1) // minimum code section body size
            {
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : Eof{VERSION}, Code is too small to be valid code");
                return false;
            }

            ushort numberOfCodeSections = container[7..9].ReadEthUInt16();
            int headerSize = CalculateHeaderSize(numberOfCodeSections);
            int pos = 3;

            if (container[pos] != KIND_TYPE)
            {
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
                return false;
            }
            pos++;
            SectionHeader typeSection = new()
            {
                Start = headerSize,
                Size = container[pos..(pos + 2)].ReadEthUInt16()
            };
            pos += 2;

            if (container[pos] != KIND_CODE)
            {
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
                return false;
            }
            pos += 3; // kind_code(1) + num_code_sections(2)
            List<SectionHeader> codeSections = new();
            int lastEndOffset = typeSection.EndOffset;
            for (ushort i = 0; i < numberOfCodeSections; i++)
            {
                SectionHeader codeSection = new()
                {
                    Start = lastEndOffset,
                    Size = container[pos..(pos + 2)].ReadEthUInt16()
                };
                codeSections.Add(codeSection);
                lastEndOffset = codeSection.EndOffset;
                pos += 2;
            }


            if (container[pos] != KIND_DATA)
            {
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
                return false;
            }
            pos++;
            SectionHeader dataSection = new()
            {
                Start = lastEndOffset,
                Size = container[(pos)..(pos + 2)].ReadEthUInt16()
            };
            pos += 2;


            if (container[pos] != TERMINATOR)
            {
                if (_loggingEnabled)
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

        bool ValidateBody(ReadOnlySpan<byte> container, in EofHeader? header)
        {
            int startOffset = CalculateHeaderSize(header.Value.CodeSections.Length);
            int calculatedCodeLength = header.Value.TypeSection.Size
                + header.Value.CodeSections.Sum(c => c.Size)
                + header.Value.DataSection.Size;

            ReadOnlySpan<byte> contractBody = container[startOffset..];

            if (contractBody.Length != calculatedCodeLength)
            {
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : SectionSizes indicated in bundeled header are incorrect, or ContainerCode is incomplete");
                return false;
            }
            return true;
        }
        bool ValidateInstructions(IReleaseSpec spec, ReadOnlySpan<byte> container, in EofHeader? header)
        {
            var (startOffset, sectionSize) = (header.Value.CodeSections[0].Start, header.Value.CodeSections[0].Size);
            ReadOnlySpan<byte> code = container.Slice(startOffset, sectionSize);
            for (int i = 0; i < sectionSize;)
            {
                Instruction? opcode = (Instruction)code[i];
                i++;
                // validate opcode
                if (!opcode.Value.IsValid(spec))
                {
                    if (_loggingEnabled)
                    {
                        _logger.Trace($"EIP-3670 : CodeSection contains undefined opcode {opcode}");
                    }
                    return false;
                }

                // Check truncated imediates
                if (opcode is >= Instruction.PUSH1 and <= Instruction.PUSH32)
                {
                    int len = code[i - 1] - (int)Instruction.PUSH1 + 1;
                    i += len;
                }

                if (i > sectionSize)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
