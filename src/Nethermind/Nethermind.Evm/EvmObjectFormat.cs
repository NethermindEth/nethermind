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
        public int CodeSize { get; set; }
        public int DataSize { get; set; }
        public byte Version { get; set; }
        public int HeaderSize => 2 + 1 + (DataSize == 0 ? 0 : (1 + 2)) + 3 + 1;
        // MagicLength + Version + 1 * (SectionSeparator + SectionSize) + HeaderTerminator = 2 + 1 + 1 * (1 + 2) + 1 = 7
        public int ContainerSize => CodeSize + DataSize;
        #endregion

        #region Equality methods
        public override bool Equals(object? obj)
            => this.GetHashCode() == obj.GetHashCode();
        public override int GetHashCode()
            => CodeSize.GetHashCode() ^ DataSize.GetHashCode();
        #endregion

        #region Sections Offsets
        public (int Start, int Size) CodeSectionOffsets => (HeaderSize, CodeSize);
        public (int Start, int Size) DataSectionOffsets => (HeaderSize + CodeSize, DataSize);
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
        public bool ExtractHeader(ReadOnlySpan<byte> code, IReleaseSpec spec, out EofHeader header)
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
                    return HandleEOF1(spec, code, ref header, codeLen, ref i);
                default:
                    if (LoggingEnabled)
                    {
                        _logger.Trace($"EIP-3540 : Code has wrong EOFn version expected {1} but found {EOFVersion}");
                    }
                    header = null; return false;
            }
        }

        private bool HandleEOF1(IReleaseSpec spec, ReadOnlySpan<byte> code, ref EofHeader header, int codeLen, ref int i)
        {
            bool continueParsing = true;

            UInt16? CodeSections = null;
            UInt16? DataSections = null;

            while (i < codeLen && continueParsing)
            {
                var sectionKind = (SectionDividor)code[i];
                i++;

                switch (sectionKind)
                {
                    case SectionDividor.Terminator:
                        {
                            if (CodeSections is null || CodeSections == 0)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : No Codesection found");
                                }
                                header = null; return false;
                            }

                            header.CodeSize = CodeSections.Value;
                            header.DataSize = DataSections ?? 0;
                            continueParsing = false;
                            break;
                        }
                    case SectionDividor.CodeSection:
                        {
                            if (i + 2 > codeLen)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : container code incomplete, failed parsing code section");
                                }
                                header = null; return false;
                            }

                            var codeSectionSize = code.Slice(i, 2).ReadEthInt16();
                            CodeSections = (UInt16)codeSectionSize;

                            if (codeSectionSize == 0) // code section must be non-empty (i.e : size > 0)
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
                            if (CodeSections is null)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-3540 : DataSection size must follow a CodeSection, CodeSection length was {0}");
                                }
                                header = null; return false;
                            }
                            if (DataSections is not null)
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

                            var dataSectionSize = code.Slice(i, 2).ReadEthInt16();
                            DataSections = (UInt16)dataSectionSize;

                            if (dataSectionSize == 0) // if declared data section must be non-empty
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

            var calculatedCodeLen = header.CodeSize + header.DataSize;

            if (contractBody.Length == 0 || calculatedCodeLen != contractBody.Length)
            {
                if (LoggingEnabled)
                {
                    _logger.Trace($"EIP-3540 : SectionSizes indicated in bundeled header are incorrect, or ContainerCode is incomplete");
                }
                header = null; return false;
            }
            return true;
        }

        public bool ValidateEofCode(IReleaseSpec spec, ReadOnlySpan<byte> code) => ExtractHeader(code, spec, out _);
        public bool ValidateInstructions(ReadOnlySpan<byte> container, out EofHeader header, IReleaseSpec spec)
        {
            if (!spec.IsEip3540Enabled)
            {
                header = null;
                return false;
            }

            if (ExtractHeader(container, spec, out header))
            {
                return true;
            }
            return false;
        }
    }
}
