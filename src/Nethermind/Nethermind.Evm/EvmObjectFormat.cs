using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Logging;
using Org.BouncyCastle.Crypto.Paddings;

namespace Nethermind.Evm
{
    enum SectionDividor : byte
    {
        Terminator = 0,
        CodeSection = 1,
        DataSection = 2,
    }
    public class EofHeader
    {
        #region public construction properties
        public UInt16 CodeSize { get; set; }
        public UInt16 DataSize { get; set; }
        public byte Version { get; set; }
        #endregion

        #region Equality methods
        public override bool Equals(object? obj)
            => this.GetHashCode() == obj.GetHashCode();
        public override int GetHashCode()
            => CodeSize.GetHashCode() ^ DataSize.GetHashCode();
        #endregion

        #region Sections Offsets
        private int? _codeStartOffset = default;
        public int CodeStartOffset
        {
            get
            {
                if (_codeStartOffset is null)
                    _codeStartOffset = DataSize == 0
                        ? 7     // MagicLength + Version + 1 * (SectionSeparator + SectionSize) + HeaderTerminator = 2 + 1 + 1 * (1 + 2) + 1 = 7
                        : 10;   // MagicLength + Version + 2 * (SectionSeparator + SectionSize) + HeaderTerminator = 2 + 1 + 2 * (1 + 2) + 1 = 10 
                return _codeStartOffset.Value;
            }
        }
        private int? _codeEndOffset = default;
        public int CodeEndOffset
        {
            get
            {
                if (_codeEndOffset is null)
                    _codeEndOffset = CodeStartOffset + CodeSize;
                return _codeEndOffset.Value;
            }
        }
        #endregion
    }

    public class EvmObjectFormat
    {
        private readonly ILogger _logger;
        private bool LoggingEnabled => _logger is not null;
        public EvmObjectFormat(ILogger logger = null)
            => _logger = logger;

        // magic prefix : EofFormatByte is the first byte, EofFormatDiff is chosen to diff from previously rejected contract according to EIP3541
        private const byte EofMagicLength = 2;
        private const byte EofFormatByte = 0xEF;
        private const byte EofFormatDiff = 0x00;
        private byte[] EofMagic => new byte[] { EofFormatByte, EofFormatDiff };

        public bool HasEOFFormat(ReadOnlySpan<byte> code) => code.Length > EofMagicLength && code.StartsWith(EofMagic);
        public bool ExtractHeader(ReadOnlySpan<byte> code, out EofHeader header)
        {
            if (!HasEOFFormat(code))
            {
                if (LoggingEnabled)
                {
                    _logger.Trace($"EIP-3540 : Code doesn't start with Magic byte sequence expected {EofMagic.ToHexString(true)} ");
                }
                header = null; return false;
            }

            int codeLen = code.Length;

            int i = EofMagicLength;
            byte EOFVersion = code[i++];

            header = new EofHeader
            {
                Version = EOFVersion
            };

            switch (EOFVersion)
            {
                case 1:
                    return HandleEOF1(code, ref header, codeLen, ref i);
                default:
                    if (LoggingEnabled)
                    {
                        _logger.Trace($"EIP-3540 : Code has wrong EOFn version expected {1} but found {EOFVersion}");
                    }
                    header = null; return false;
            }
        }

        private bool HandleEOF1(ReadOnlySpan<byte> code, ref EofHeader header, int codeLen, ref int i)
        {
            bool continueParsing = true;

            while (i < codeLen && continueParsing)
            {
                var sectionKind = (SectionDividor)code[i];
                i++;

                switch (sectionKind)
                {
                    case SectionDividor.Terminator:
                        {
                            if (header.CodeSize == 0)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : CodeSection size must follow a CodeSection, CodeSection length was {header.CodeSize}");
                                }
                                header = null; return false;
                            }

                            continueParsing = false;
                            break;
                        }
                    case SectionDividor.CodeSection:
                        {
                            // code-section must come first and there can be only one data-section
                            if (header.CodeSize > 0)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : container must have exactly 1 CodeSection but found more");
                                }
                                header = null; return false;
                            }

                            if (i + 2 > codeLen)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : container code incomplete, failed parsing code section");
                                }
                                header = null; return false;
                            }

                            var codeSectionSlice = code.Slice(i, 2);
                            header.CodeSize = (ushort)((codeSectionSlice[0] << 8) | codeSectionSlice[1]);

                            if (header.CodeSize == 0) // code section must be non-empty (i.e : size > 0)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : CodeSection size must be strictly bigger than 0 but found 0");
                                }
                                header = null; return false;
                            }

                            i += 2;
                            break;
                        }
                    case SectionDividor.DataSection:
                        {
                            // data-section must come after code-section and there can be only one data-section
                            if (header.CodeSize == 0)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : CodeSection size must follow a CodeSection, CodeSection length was {header.CodeSize}");
                                }
                                header = null; return false;
                            }
                            if (header.DataSize != 0)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : container must have at max 1 DataSection but found more");
                                }
                                header = null; return false;
                            }

                            if (i + 2 > codeLen)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : container code incomplete, failed parsing data section");
                                }
                                header = null; return false;
                            }

                            var dataSectionSlice = code.Slice(i, 2);
                            header.DataSize = (ushort)((dataSectionSlice[0] << 8) | dataSectionSlice[1]);

                            if (header.DataSize == 0) // if declared data section must be non-empty
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : DataSection size must be strictly bigger than 0 but found 0");
                                }
                                header = null; return false;
                            }

                            i += 2;
                            break;
                        }
                    default: // if section kind is anything beside a section-limiter or a terminator byte we return false
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-3540 : Encountered incorrect Section-Kind {sectionKind}, correct values are [{SectionDividor.CodeSection}, {SectionDividor.DataSection}, {SectionDividor.Terminator}]");
                            }

                            header = null; return false;
                        }
                }
            }
            var contractBody = code[i..];
            var calculatedCodeLen = (int)header.CodeSize + (int)header.DataSize;
            if (header.CodeSize == 0 || contractBody.Length == 0 || calculatedCodeLen != contractBody.Length)
            {
                if (LoggingEnabled)
                {
                    _logger.Trace($"EIP-3540 : SectionSizes indicated in bundeled header are incorrect, or ContainerCode is incomplete");
                }
                header = null; return false;
            }
            return true;
        }

        public bool ValidateEofCode(ReadOnlySpan<byte> code) => ExtractHeader(code, out _);
        public bool ValidateInstructions(ReadOnlySpan<byte> code, out EofHeader header, IReleaseSpec spec)
        {
            // check if code is EOF compliant
            if (!spec.IsEip3540Enabled)
            {
                header = null;
                return false;
            }

            if (ExtractHeader(code, out header))
            {
                if (!spec.IsEip3670Enabled)
                {
                    return true;
                }

                var (startOffset, endOffset) = (header.CodeStartOffset, header.CodeEndOffset);
                Instruction? opcode = null;
                HashSet<Range> immediates = new HashSet<Range>();
                HashSet<Int32> rjumpdests = new HashSet<Int32>();
                for (int i = startOffset; i < endOffset;)
                {
                    opcode = (Instruction)code[i];

                    // validate opcode
                    if (!Enum.IsDefined(opcode.Value))
                    {
                        if (LoggingEnabled)
                        {
                            _logger.Trace($"EIP-3670 : CodeSection contains undefined opcode {opcode}");
                        }
                        return false;
                    }

                    if (spec.IsEip4200Enabled)
                    {
                        if (opcode is Instruction.RJUMP or Instruction.RJUMPI)
                        {
                            if (i + 3 > endOffset)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-4200 : Static Relative Jump Argument underflow");
                                }
                                return false;
                            }

                            var offset = code[(i + 1)..(i + 3)].ReadEthInt16();
                            immediates.Add(new Range(i + 1, i + 2));
                            var rjumpdest = offset + 3 + i;
                            rjumpdests.Add(rjumpdest);
                            if (rjumpdest < startOffset || rjumpdest >= endOffset)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-4200 : Static Relative Jump Destination outside of Code bounds");
                                }
                                return false;
                            }
                            i += 2;
                        }
                    }

                    if (opcode is >= Instruction.PUSH1 and <= Instruction.PUSH32)
                    {
                        int len = code[i] - (int)Instruction.PUSH1 + 1;
                        immediates.Add(new Range(i + 1, i + len));
                        i += len;
                        if (i >= endOffset)
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-3670 : Last opcode {opcode} in CodeSection should be either [{Instruction.STOP}, {Instruction.RETURN}, {Instruction.REVERT}, {Instruction.INVALID}, {Instruction.SELFDESTRUCT}");
                            }
                            return false;
                        }
                    }
                    i++;
                }

                bool endCorrectly = opcode switch
                {
                    Instruction.STOP or Instruction.RETURN or Instruction.REVERT or Instruction.INVALID or Instruction.SELFDESTRUCT
                        => true,
                    _ => false
                };

                if (!endCorrectly)
                {
                    if (LoggingEnabled)
                    {
                        _logger.Trace($"EIP-3670 : Last opcode {opcode} in CodeSection should be either [{Instruction.STOP}, {Instruction.RETURN}, {Instruction.REVERT}, {Instruction.INVALID}, {Instruction.SELFDESTRUCT}");
                    }
                    return false;
                }

                if (spec.IsEip4200Enabled)
                {

                    foreach (int rjumpdest in rjumpdests)
                    {
                        foreach (var range in immediates)
                        {
                            if (range.Includes(rjumpdest))
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-4200 : Static Relative Jump destination {rjumpdest} is an Invalid, falls within {range}");
                                }
                                return false;
                            }
                        }
                    }
                }
                return true;
            }
            return false;
        }
    }
}
