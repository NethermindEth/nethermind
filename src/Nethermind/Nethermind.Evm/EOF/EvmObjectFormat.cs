using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Specs;

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
    public EvmObjectFormat(IReleaseSpec releaseSpec, ILogManager logManager = null)
    {
        _logger = logManager?.GetClassLogger<EvmObjectFormat>();
        _eofVersionHandlers.Add(0x01, new Eof1(releaseSpec, logManager));
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
        private IReleaseSpec _releaseSpec;
        private const byte VERSION = 0x01;
        private const byte KIND_TYPE = 0x01;
        private const byte KIND_CODE = 0x02;
        private const byte KIND_DATA = 0x03;
        private const byte TERMINATOR = 0x00;
        private const byte VERSION_SIZE = 1;
        private const byte SECTION_SIZE = 3;
        private const byte TERMINATOR_SIZE = 1;
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
        private bool _loggingEnabled => _logger?.IsTrace ?? false;

        public Eof1(IReleaseSpec spec, ILogManager? logManager = null)
        {
            _logger = logManager?.GetClassLogger<Eof1>();
            _releaseSpec = spec;
        }

        public bool ValidateCode(ReadOnlySpan<byte> container, out EofHeader? header)
        {
            return TryParseEofHeader(container, out header)
                && ValidateBody(container, ref header)
                && ValidateInstructions(container, ref header);
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
            if(numberOfCodeSections < 1)
            {
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : At least one code section must be present");
                return false;
            }

            int headerSize = CalculateHeaderSize(numberOfCodeSections);
            int pos = 3;

            if (container[pos] != KIND_TYPE)
            {
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : Eof{VERSION}, Code header is not well formatted");
                return false;
            }

            pos++;
            if (!CheckBounds(pos + 2, container.Length, ref header))
            {
                return false;
            }

            SectionHeader typeSection = new()
            {
                Start = headerSize,
                Size = container[pos..(pos + 2)].ReadEthUInt16()
            };

            if(typeSection.Size < 3)
            {
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : TypeSection Size must be at least 3, but found {typeSection.Size}");
                return false;
            }

            pos += 2;

            if (container[pos] != KIND_CODE)
            {
                if (_loggingEnabled)
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
                if (!CheckBounds(pos + 2, container.Length, ref header))
                {
                    return false;
                }
                SectionHeader codeSection = new()
                {
                    Start = lastEndOffset,
                    Size = container[pos..(pos + 2)].ReadEthUInt16()
                };

                if (codeSection.Size == 0)
                {
                    if (_loggingEnabled)
                        _logger.Trace($"EIP-3540 : Empty Code Section are not allowed, CodeSectionSize must be > 0 but found {codeSection.Size}");
                    return false;
                }

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
            if (!CheckBounds(pos + 2, container.Length, ref header))
            {
                return false;
            }

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
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : SectionSizes indicated in bundeled header are incorrect, or ContainerCode is incomplete");
                return false;
            }
            return true;
        }
        bool ValidateInstructions(ReadOnlySpan<byte> container, ref EofHeader? header)
        {
            if (!_releaseSpec.IsEip3670Enabled)
            {
                return true;
            }

            var (startOffset, sectionSize) = (header.Value.CodeSections[0].Start, header.Value.CodeSections[0].Size);
            ReadOnlySpan<byte> code = container.Slice(startOffset, sectionSize);
            for (int i = 0; i < sectionSize;)
            {
                Instruction? opcode = (Instruction)code[i];
                i++;
                // validate opcode
                if (!opcode.Value.IsValid(_releaseSpec))
                {
                    if (_loggingEnabled)
                    {
                        _logger.Trace($"EIP-3670 : CodeSection contains undefined opcode {opcode}");
                    }
                    header = null; return false;
                }

                // Check truncated imediates
                if (opcode is >= Instruction.PUSH1 and <= Instruction.PUSH32)
                {
                    int len = code[i - 1] - (int)Instruction.PUSH1 + 1;
                    i += len;
                }

                if (i > sectionSize)
                {
                    if (_loggingEnabled)
                    {
                        _logger.Trace($"EIP-3670 : PC Reached out of bounds");
                    }
                    header = null; return false;
                }
            }
            return true;
        }
    }
}
