using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Attributes;
using Nethermind.Evm.CodeAnalysis;
using Org.BouncyCastle.Crypto.Paddings;

namespace Nethermind.Evm
{
    public class EofHeader
    {
        public UInt16 CodeSize { get; set; }
        public UInt16 DataSize { get; set; }
        public override bool Equals(object? obj)
            => this.GetHashCode() == obj.GetHashCode();

        public override int GetHashCode()
            => CodeSize.GetHashCode() ^ DataSize.GetHashCode();
    }


    [Eip(3540, Phase.Draft)]
    public class EvmObjectFormat
    {
        // magic prefix : EofFormatByte is the first byte, EofFormatDiff is chosen to diff from previously rejected contract according to EIP3541
        private const byte EofMagicLength = 2;
        private const byte EofFormatByte = 0xEF;
        private const byte EofFormatDiff = 0x00;

        private const byte EofVersion = 1;
        private const byte SectionKindTerminator = 0;
        private const byte SectionKindCode = 1;
        private const byte SectionKindData = 2;

        private byte[] EofMagic => new byte[]{ EofFormatByte, EofFormatDiff };

        public bool HasEOFFormat(Span<byte> code) => code.Length >= EofMagicLength && code.StartsWith(EofMagic);
        public bool ExtractHeader(Span<byte> code, out EofHeader header)
        {
            if (!HasEOFFormat(code))
            {
                header = null; return false;
            }

            int codeLen = code.Length;

            int i = EofMagicLength;

            if(i >= codeLen || code[i] != EofVersion)
            {
                header = null;  return false;
            }
            i++;


            header = new EofHeader { };
            while (i < codeLen)
            {
                var sectionKind = code[i];
                i++;
                if (sectionKind == SectionKindTerminator)
                {
                    break;
                }
                else if (sectionKind == SectionKindCode)
                {
                    if (header.CodeSize > 0 || i + 2 > codeLen)
                    {
                        header = null; return false;
                    }

                    var codeSectionSlice = code.Slice(i, 2);
                    codeSectionSlice.Reverse();
                    header.CodeSize = BitConverter.ToUInt16(codeSectionSlice);

                    if (header.CodeSize == 0)
                    {
                        header = null; return false;
                    }

                    i += 2;
                }
                else if (sectionKind == SectionKindData)
                {
                    if (header.CodeSize == 0 || header.DataSize != 0 || i + 2 > codeLen)
                    {
                        header = null; return false;
                    }

                    var dataSectionSlice = code.Slice(i, 2);
                    dataSectionSlice.Reverse();
                    header.DataSize = BitConverter.ToUInt16(dataSectionSlice);

                    if (header.DataSize == 0)
                    {
                        header = null; return false;
                    }

                    i += 2;
                }
                else
                {
                    header = null; return false;
                }
            }
            var calculatedCodeLen = (int)header.CodeSize + (int)header.DataSize + i;
            if ((header.CodeSize == 0) || (calculatedCodeLen != codeLen))
            {
                header = null; return false;
            }

            return true;
        }

        public bool ValidateEofCode(Span<byte> code) => ExtractHeader(code, out _);
        public bool ValidateEofCode(byte[] code) => ExtractHeader(code, out _);
        
        public (int StartOffset, int EndOffset)? ExtractCodeOffsets(byte[] code)
        {
            if(ExtractHeader(code, out var header))
            {
                return ExtractCodeOffsets(header);
            }
            return null;
        }

        public int codeStartOffset(EofHeader header) => header.DataSize == 0
                                                            ? 5 + EofMagicLength  // magic (2b) + version(1b) +  1 * (sectionId(1b) + sectionSize(2b)) + separator(1b) = magic (2b) + 5b
                                                            : 8 + EofMagicLength; // magic (2b) + version(1b) +  2 * (sectionId(1b) + sectionSize(2b)) + separator(1b) = magic (2b) + 8b
        public int codeEndOffset(EofHeader header)  => codeStartOffset(header) + header.CodeSize;

        public (int StartOffset, int EndOffset) ExtractCodeOffsets(EofHeader header)
        {
            var endIndex = codeEndOffset(header);
            var strIndex = endIndex - header.CodeSize;
            return (strIndex, endIndex);
        }

        public bool ValidateInstructions(Span<byte> code, out EofHeader header)
        {
            // check if code is EOF compliant
            if(ExtractHeader(code, out header))
            {
                var (startOffset, endOffset) = ExtractCodeOffsets(header);
                Instruction? opcode = null;
                for (int i = startOffset; i < endOffset;)
                {
                    opcode = (Instruction)code[i];

                    // validate opcode
                    if (!Enum.IsDefined(typeof(Instruction), code[i]))
                    {
                        return false;
                    }

                    if (opcode is >= Instruction.PUSH1 and <= Instruction.PUSH32)
                    {
                        i += code[i] - (int)Instruction.PUSH1 + 1;
                    }
                    i++;
                }

                // check if terminating opcode : STOP, RETURN, REVERT, INVALID, SELFDESTRUCT
                switch (opcode)
                {
                    case Instruction.STOP:
                    case Instruction.RETURN:
                    case Instruction.REVERT:
                    case Instruction.INVALID:
                    case Instruction.SELFDESTRUCT: // might be retired and replaced with SELLALL?
                        return true;
                    default:
                        return false;
                }
            } return false;
        }
    }
}
