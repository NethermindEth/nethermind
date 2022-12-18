using System;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;

namespace Nethermind.Evm.EOF;

public class EvmObjectFormat
{
    private readonly ILogger _logger;
    private bool LoggingEnabled => _logger is not null;
    public EvmObjectFormat(ILogManager logManager = null)
        => _logger = logManager?.GetClassLogger<EvmObjectFormat>();

    // magic prefix : EofFormatByte is the first byte, EofFormatDiff is chosen to diff from previously rejected contract according to EIP3541
    private static byte[] EofMagic = { 0xEF, 0x00 };

    public bool HasEofFormat(ReadOnlySpan<byte> code) => code.Length > EofMagic.Length && code.StartsWith(EofMagic);
    public bool ExtractHeader(ReadOnlySpan<byte> code, IReleaseSpec spec, out EofHeader? header)
    {
        if (!HasEofFormat(code))
        {
            if (LoggingEnabled)
                _logger.Trace($"EIP-3540 : Code doesn't start with Magic byte sequence expected {EofMagic.ToHexString(true)} ");
            header = null; return false;
        }

        int codeLen = code.Length;

        int i = EofMagic.Length;
        byte eofVersion = code[i++];

        header = new EofHeader
        {
            Version = eofVersion
        };

        switch (eofVersion)
        {
            case 1:
                return HandleEof1(spec, code, ref header, codeLen, ref i);
            default:
                if (LoggingEnabled)
                    _logger.Trace($"EIP-3540 : Code has wrong EOFn version expected {1} but found {eofVersion}");
                header = null; return false;
        }
    }

    private bool HandleEof1(IReleaseSpec spec, ReadOnlySpan<byte> code, ref EofHeader? header, int codeLen, ref int i)
    {
        bool continueParsing = true;
        ushort? codeSectionSize = null;
        ushort? dataSectionSize = null;
        while (i < codeLen && continueParsing)
        {
            var sectionKind = (SectionDividor)code[i];
            i++;

            switch (sectionKind)
            {
                case SectionDividor.Terminator:
                    {
                        if (codeSectionSize is null)
                        {
                            if (LoggingEnabled)
                                _logger.Trace($"EIP-3540 : No Codesection found");
                            header = null; return false;
                        }
                        int? codeSectionStart = dataSectionSize is null
                            ? 7
                            : 10;
                        int? dataSectionStart = codeSectionStart + codeSectionSize;
                        header = header.Value with
                        {
                            CodeSection = new SectionHeader
                            {
                                Size = codeSectionSize.Value,
                                Start = codeSectionStart.Value
                            },
                            DataSection = dataSectionSize is null
                            ? null
                            : new SectionHeader
                            {
                                Size = dataSectionSize.Value,
                                Start = dataSectionStart.Value
                            }
                        };
                        continueParsing = false;
                        break;
                    }
                case SectionDividor.CodeSection:
                    {
                        if (codeSectionSize is not null)
                        {
                            if (LoggingEnabled)
                                _logger.Trace($"EIP-3540 : only 1 code section is allowed");
                            header = null; return false;
                        }

                        if (i + 2 > codeLen)
                        {
                            if (LoggingEnabled)
                                _logger.Trace($"EIP-3540 : container code incomplete, failed parsing code section");
                            header = null; return false;
                        }

                        codeSectionSize = code.Slice(i, 2).ReadEthUInt16();
                        if (codeSectionSize == 0) // code section must be non-empty (i.e : size > 0)
                        {
                            if (LoggingEnabled)
                                _logger.Trace($"EIP-3540 : CodeSection size must be strictly bigger than 0 but found 0");
                            header = null; return false;
                        }

                        i += 2;
                        break;
                    }
                case SectionDividor.DataSection:
                    {
                        // data-section must come after code-section and there can be only one data-section
                        if (codeSectionSize is null)
                        {
                            if (LoggingEnabled)
                                _logger.Trace($"EIP-3540 : DataSection size must follow a CodeSection, CodeSection length was {0}");
                            header = null; return false;
                        }

                        if (dataSectionSize is not null)
                        {
                            if (LoggingEnabled)
                                _logger.Trace($"EIP-3540 : container must have at max 1 DataSection but found more");
                            header = null; return false;
                        }

                        if (i + 2 > codeLen)
                        {
                            if (LoggingEnabled)
                                _logger.Trace($"EIP-3540 : container code incomplete, failed parsing data section");
                            header = null; return false;
                        }

                        dataSectionSize = code.Slice(i, 2).ReadEthUInt16();

                        if (dataSectionSize == 0) // if declared data section must be non-empty
                        {
                            if (LoggingEnabled)
                                _logger.Trace($"EIP-3540 : DataSection size must be strictly bigger than 0 but found 0");
                            header = null; return false;
                        }

                        i += 2;
                        break;
                    }
                default: // if section kind is anything beside a section-limiter or a terminator byte we return false
                    {
                        if (LoggingEnabled)
                            _logger.Trace($"EIP-3540 : Encountered incorrect Section-Kind {sectionKind}, correct values are [{SectionDividor.CodeSection}, {SectionDividor.DataSection}, {SectionDividor.Terminator}]");

                        header = null; return false;
                    }
            }
        }
        var contractBody = code[i..];

        var calculatedCodeLen = header.Value.CodeSection.Size + (header.Value.DataSection?.Size ?? 0);

        if (contractBody.Length == 0 || calculatedCodeLen != contractBody.Length)
        {
            if (LoggingEnabled)
                _logger.Trace($"EIP-3540 : SectionSizes indicated in bundeled header are incorrect, or ContainerCode is incomplete");
            header = null; return false;
        }
        return true;
    }

    public bool ValidateEofCode(IReleaseSpec spec, ReadOnlySpan<byte> code) => ExtractHeader(code, spec, out _);
    public bool ValidateInstructions(ReadOnlySpan<byte> container, out EofHeader? header, IReleaseSpec spec)
    {
        if (!spec.IsEip3540Enabled)
        {
            header = null;
            return false;
        }

        if (ExtractHeader(container, spec, out header))
        {
            if (!spec.IsEip3670Enabled)
            {
                return true;
            }

            var (startOffset, sectionSize) = (header.Value.CodeSection.Start, header.Value.CodeSection.Size);
            ReadOnlySpan<byte> code = container.Slice(startOffset, sectionSize);
            Instruction? opcode = null;
            for (int i = 0; i < sectionSize;)
            {
                opcode = (Instruction)code[i];
                i++;
                // validate opcode
                if (!opcode.Value.IsValid(spec))
                {
                    if (LoggingEnabled)
                    {
                        _logger.Trace($"EIP-3670 : CodeSection contains undefined opcode {opcode}");
                    }
                    header = null; return false;
                }

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
        return false;
    }
}
