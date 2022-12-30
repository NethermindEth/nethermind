using System;
using System.Linq;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
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
    private bool _loggingEnabled => _logger?.IsTrace ?? false;
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

        private const byte MINIMUM_TYPESECTION_SIZE = 4;
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
            if (container[EOF_MAGIC.Length] != VERSION)
            {
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : Code is not Eof version {VERSION}");
                return false;
            }

            if (container.Length < MINIMUM_HEADER_SIZE
                + MINIMUM_TYPESECTION_SIZE // minimum type section body size
                + MINIMUM_CODESECTION_SIZE) // minimum code section body size
            {
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : Eof{VERSION}, Code is too small to be valid code");
                return false;
            }


            ushort numberOfCodeSections = container[7..9].ReadEthUInt16();
            if (numberOfCodeSections < MINIMUM_CODESECTIONS_COUNT)
            {
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : At least one code section must be present");
                return false;
            }

            if (numberOfCodeSections > MAXIMUM_CODESECTIONS_COUNT)
            {
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : code sections count must not exceed 1024");
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
                if (_loggingEnabled)
                    _logger.Trace($"EIP-3540 : TypeSection Size must be at least 4, but found {typeSection.Size}");
                return false;
            }

            pos += IMMEDIATE_16BIT_BYTE_COUNT;

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
                if (!CheckBounds(pos + IMMEDIATE_16BIT_BYTE_COUNT, container.Length, ref header))
                {
                    header = null;
                    return false;
                }

                SectionHeader codeSection = new()
                {
                    Start = lastEndOffset,
                    Size = container[pos..(pos + IMMEDIATE_16BIT_BYTE_COUNT)].ReadEthUInt16()
                };

                if (codeSection.Size == 0)
                {
                    if (_loggingEnabled)
                        _logger.Trace($"EIP-3540 : Empty Code Section are not allowed, CodeSectionSize must be > 0 but found {codeSection.Size}");

                    header = null;
                    return false;
                }

                codeSections.Add(codeSection);
                lastEndOffset = codeSection.EndOffset;
                pos += IMMEDIATE_16BIT_BYTE_COUNT;

            }


            if (container[pos] != KIND_DATA)
            {
                if (_loggingEnabled)
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
            SectionHeader[]? codeSections = header.Value.CodeSections;
            (int typeSectionStart, ushort typeSectionSize) = header.Value.TypeSection;
            if (codeSections.Length == 0 || codeSections.Any(section => section.Size == 0))
            {
                if (_loggingEnabled)
                {
                    _logger.Trace($"EIP-3540 : CodeSection size must follow a CodeSection, CodeSection length was {codeSections.Length}");
                }

                header = null;
                return false;
            }

            if (codeSections.Length != (typeSectionSize / MINIMUM_TYPESECTION_SIZE))
            {
                if (_loggingEnabled)
                {
                    _logger.Trace($"EIP-4750: Code Sections count must match TypeSection count, CodeSection count was {codeSections.Length}, expected {typeSectionSize / MINIMUM_TYPESECTION_SIZE}");
                }

                header = null;
                return false;
            }

            if (container[typeSectionStart] != 0 && container[typeSectionStart] != 0)
            {
                if (_loggingEnabled)
                {
                    _logger.Trace($"EIP-4750: first 2 bytes of type section must be 0s");
                }

                header = null;
                return false;
            }

            if (codeSections.Length > MAXIMUM_CODESECTIONS_COUNT)
            {
                if (_loggingEnabled)
                {
                    _logger.Trace($"EIP-4750 : Code section count limit exceeded only {MAXIMUM_CODESECTIONS_COUNT} allowed but found {codeSections.Length}");
                }

                header = null;
                return false;
            }

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
        public bool ValidateInstructions(ReadOnlySpan<byte> container, ref EofHeader? header)
        {
            if (!_releaseSpec.IsEip3670Enabled)
            {
                return true;
            }

            bool valid = true;
            for (int i = 0; valid && i < header.Value.CodeSections.Length; i++)
            {
                valid &= ValidateSectionInstructions(ref container, i, ref header);
            }
            return valid;
        }

        public bool ValidateSectionInstructions(ref ReadOnlySpan<byte> container, int sectionId, ref EofHeader? header)
        {
            (int typeSectionBegin, ushort typeSectionSize) = header.Value.TypeSection;
            (int codeSectionBegin, ushort codeSectionSize) = header.Value.CodeSections[sectionId];

            ReadOnlySpan<byte> code = container.Slice(codeSectionBegin, codeSectionSize);
            ReadOnlySpan<byte> typesection = container.Slice(typeSectionBegin, typeSectionSize);
            Instruction? opcode = null;

            HashSet<Range> immediates = new();
            HashSet<Int32> rjumpdests = new();
            for (int i = 0; i < codeSectionSize;)
            {
                opcode = (Instruction)code[i];
                i++;
                // validate opcode
                if (!opcode.Value.IsValid(_releaseSpec))
                {
                    if (_loggingEnabled)
                    {
                        _logger.Trace($"EIP-3670 : CodeSection contains undefined opcode {opcode}");
                    }
                    header = null;
                    return false;
                }

                if (_releaseSpec.StaticRelativeJumpsEnabled)
                {
                    if (opcode is Instruction.RJUMP or Instruction.RJUMPI)
                    {
                        if (i + IMMEDIATE_16BIT_BYTE_COUNT > codeSectionSize)
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : Static Relative Jumpv Argument underflow");
                            }

                            header = null;
                            return false;
                        }

                        var offset = code.Slice(i, IMMEDIATE_16BIT_BYTE_COUNT).ReadEthInt16();
                        immediates.Add(new Range(i, i + 1));
                        var rjumpdest = offset + IMMEDIATE_16BIT_BYTE_COUNT + i;
                        rjumpdests.Add(rjumpdest);
                        if (rjumpdest < 0 || rjumpdest >= codeSectionSize)
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : Static Relative Jump Destination outside of Code bounds");
                            }
                            header = null;
                            return false;
                        }
                        i += 2;
                    }

                    if (opcode is Instruction.RJUMPV)
                    {
                        if (i + IMMEDIATE_16BIT_BYTE_COUNT > codeSectionSize)
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : Static Relative Jumpv Argument underflow");
                            }
                            header = null;
                            return false;
                        }

                        byte count = code[i];
                        if (count < 1)
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : jumpv jumptable must have at least 1 entry");
                            }
                            header = null;
                            return false;
                        }

                        if (i + count * IMMEDIATE_16BIT_BYTE_COUNT > codeSectionSize)
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : jumpv jumptable underflow");
                            }
                            header = null;
                            return false;
                        }

                        var immediateValueSize = 1 + count * IMMEDIATE_16BIT_BYTE_COUNT;
                        immediates.Add(new Range(i, i + immediateValueSize - 1));
                        for (int j = 0; j < count; j++)
                        {
                            var offset = code.Slice(i + 1 + j * IMMEDIATE_16BIT_BYTE_COUNT, IMMEDIATE_16BIT_BYTE_COUNT).ReadEthInt16();
                            var rjumpdest = offset + immediateValueSize + i;
                            rjumpdests.Add(rjumpdest);
                            if (rjumpdest < 0 || rjumpdest >= codeSectionSize)
                            {
                                if (_loggingEnabled)
                                {
                                    _logger.Trace($"EIP-4200 : Static Relative Jumpv Destination outside of Code bounds");
                                }
                                header = null;
                                return false;
                            }
                        }
                        i += immediateValueSize;
                    }
                }

                if (_releaseSpec.FunctionSections)
                {
                    if (opcode is Instruction.CALLF)
                    {
                        if (i + IMMEDIATE_16BIT_BYTE_COUNT > codeSectionSize)
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-4750 : CALLF Argument underflow");
                            }
                            header = null;
                            return false;
                        }

                        ushort targetSectionId = code.Slice(i, IMMEDIATE_16BIT_BYTE_COUNT).ReadEthUInt16();
                        immediates.Add(new Range(i, i + 1));

                        if (targetSectionId >= header.Value.CodeSections.Length)
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-4750 : Invalid Section Id");
                            }
                            header = null;
                            return false;
                        }
                        i += IMMEDIATE_16BIT_BYTE_COUNT;
                    }
                }

                if (opcode is >= Instruction.PUSH1 and <= Instruction.PUSH32)
                {
                    int len = code[i - 1] - (int)Instruction.PUSH1 + 1;
                    immediates.Add(new Range(i, i + len - 1));
                    i += len;
                }

                if (i > codeSectionSize)
                {
                    if (_loggingEnabled)
                    {
                        _logger.Trace($"EIP-3670 : PC Reached out of bounds");
                    }
                    header = null;
                    return false;
                }
            }

            if (_releaseSpec.StaticRelativeJumpsEnabled)
            {

                foreach (int rjumpdest in rjumpdests)
                {
                    foreach (Range range in immediates)
                    {
                        if (range.Includes(rjumpdest))
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : Static Relative Jump destination {rjumpdest} is an Invalid, falls within {range}");
                            }
                            header = null;
                            return false;
                        }
                    }
                }
            }
            return true;
        }
    }
}
