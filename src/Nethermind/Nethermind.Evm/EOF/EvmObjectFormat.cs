using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;

namespace Nethermind.Evm.EOF;

public class EvmObjectFormat
{
    private readonly ILogger _logger = null;
    private bool LoggingEnabled => _logger is not null;
    public EvmObjectFormat(ILogManager logManager = null)
        => _logger = logManager?.GetClassLogger<EvmObjectFormat>();

    // magic prefix : EofFormatByte is the first byte, EofFormatDiff is chosen to diff from previously rejected contract according to EIP3541
    private static readonly byte[] EofMagic = { 0xEF, 0x00 };

    public bool HasEofFormat(ReadOnlySpan<byte> code) => code.Length > EofMagic.Length && code.StartsWith(EofMagic);
    internal bool ExtractHeader(ReadOnlySpan<byte> code, IReleaseSpec spec, out EofHeader? header)
    {
        if (!HasEofFormat(code))
        {
            if (LoggingEnabled)
            {
                _logger.Trace($"EIP-3540 : Code doesn't start with Magic byte sequence expected {EofMagic.ToHexString(true)} ");
            }
            header = null; return false;
        }

        int codeLen = code.Length;

        int i = EofMagic.Length;
        byte EOFVersion = code[i++];

        header = new EofHeader
        {
            Version = EOFVersion
        };

        switch (EOFVersion)
        {
            case 1:
                return HandleEof1(spec, code, ref header, codeLen, ref i);
            default:
                if (LoggingEnabled)
                {
                    _logger.Trace($"EIP-3540 : Code has wrong EOFn version expected {1} but found {EOFVersion}");
                }
                header = null; return false;
        }
    }

    private bool HandleEof1(IReleaseSpec spec, ReadOnlySpan<byte> code, ref EofHeader? header, int codeLen, ref int i)
    {
        bool continueParsing = true;

        List<ushort> codeSections = new();
        ushort? typeSections = null;
        ushort? dataSections = null;

        while (i < codeLen && continueParsing)
        {
            var sectionKind = (SectionDividor)code[i];
            i++;

            switch (sectionKind)
            {
                case SectionDividor.Terminator:
                    {
                        if (codeSections.Count == 0 || codeSections.Any(section => section == 0))
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-3540 : CodeSection size must follow a CodeSection, CodeSection length was {codeSections.Count}");
                            }
                            header = null; return false;
                        }

                        if (codeSections.Count > 1 && codeSections.Count != (typeSections / 2))
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-4750: Code Sections count must match TypeSection count, CodeSection count was {codeSections.Count}, expected {typeSections / 2}");
                            }
                            header = null; return false;
                        }

                        if (codeSections.Count > 1024)
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-4750 : Code section count limit exceeded only 1024 allowed but found {codeSections.Count}");
                            }
                            header = null; return false;
                        }

                        int HeaderSize = 2 + 1 + (typeSections is null ? 0 : 1 + 2) + (dataSections is null ? 0 : 1 + 2) + (3 * codeSections.Count) + 1;

                        var codeSectionHeaders = new SectionHeader[codeSections.Count];
                        int accumulatedOffset = 0;

                        for (int j = 0; j < codeSections.Count; j++)
                        {
                            codeSectionHeaders[j] = new SectionHeader
                            {
                                Size = codeSections[j],
                                Start = accumulatedOffset,
                            };
                            accumulatedOffset += codeSections[j];
                        }

                        header = header.Value with
                        {
                            TypeSection = typeSections is null
                                ? null
                                : new SectionHeader
                                {
                                    Size = typeSections.Value,
                                    Start = HeaderSize
                                },
                            CodeSections = new SectionHeaderCollection
                            {
                                ChildSections = codeSectionHeaders,
                                Start = HeaderSize + (typeSections ?? 0)
                            },
                            DataSection = dataSections is null
                                ? null
                                : new SectionHeader
                                {
                                    Size = dataSections.Value,
                                    Start = HeaderSize + (typeSections ?? 0) + accumulatedOffset
                                }
                        };
                        continueParsing = false;
                        break;
                    }
                case SectionDividor.TypeSection:
                    {
                        if (spec.IsEip4750Enabled)
                        {
                            if (dataSections is not null || codeSections.Count != 0)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-4750 : TypeSection must be before : CodeSection, DataSection");
                                }
                                header = null; return false;
                            }

                            if (typeSections is not null)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-4750 : container must have at max 1 TypeSection but found more");
                                }
                                header = null; return false;
                            }

                            if (i + 2 > codeLen)
                            {
                                if (LoggingEnabled)
                                {
                                    _logger.Trace($"EIP-4750: type section code incomplete, failed parsing type section");
                                }
                                header = null; return false;
                            }

                            typeSections = code.Slice(i, 2).ReadEthUInt16();
                        }
                        else
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-3540 : Encountered incorrect Section-Kind {sectionKind}, correct values are [{SectionDividor.CodeSection}, {SectionDividor.DataSection}, {SectionDividor.Terminator}]");
                            }

                            header = null; return false;
                        }
                        i += 2;
                        break;
                    }
                case SectionDividor.CodeSection:
                    {
                        if (!spec.IsEip4750Enabled && codeSections.Count > 0)
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-3540 : only 1 code section is allowed");
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

                        var codeSectionSize = code.Slice(i, 2).ReadEthUInt16();
                        codeSections.Add(codeSectionSize);

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
                        if (codeSections.Count == 0)
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-3540 : DataSection size must follow a CodeSection, CodeSection length was {codeSections.Count}");
                            }
                            header = null; return false;
                        }

                        if (dataSections is not null)
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

                        dataSections = code.Slice(i, 2).ReadEthUInt16();

                        if (dataSections == 0) // if declared data section must be non-empty
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
                            _logger.Trace($"EIP-3540 : Encountered incorrect Section-Kind {sectionKind}, correct values are [{SectionDividor.TypeSection}, {SectionDividor.CodeSection}, {SectionDividor.DataSection}, {SectionDividor.Terminator}]");
                        }

                        header = null; return false;
                    }
            }
        }
        var contractBody = code[i..];

        var calculatedCodeLen = (header.Value.TypeSection?.Size ?? 0) + (header.Value.CodeSections.Size) + (header.Value.DataSection?.Size ?? 0);
        if (spec.IsEip4750Enabled && header.Value.TypeSection is not null && contractBody.Length > 1 && contractBody[0] != 0 && contractBody[1] != 0)
        {
            if (LoggingEnabled)
            {
                _logger.Trace($"EIP-4750: Invalid Type Section expected {(0, 0)} but found {(contractBody[0], contractBody[1])}");
            }
            header = null; return false;
        }

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

    internal bool ValidateEofCode(ReadOnlySpan<byte> code, IReleaseSpec spec) => ValidateInstructions(code, spec, out _);

    internal bool ValidateInstructions(ReadOnlySpan<byte> container, IReleaseSpec spec, out EofHeader? header)
    {
        // check if code is EOF compliant
        if (!spec.IsEip3540Enabled)
        {
            header = null; return false;
        }

        if (ExtractHeader(container, spec, out header))
        {
            if (spec.IsEip3670Enabled)
            {
                bool valid = true;
                for (ushort sectionId = 0; sectionId < header.Value.CodeSections.Count; sectionId++)
                {
                    var (startOffset, sectionSize) = header.Value.CodeSections[sectionId];
                    ReadOnlySpan<byte> code = container.Slice(header.Value.CodeSections.Start + startOffset, sectionSize);

                    (startOffset, sectionSize) = header.Value.TypeSection.HasValue
                        ? (header.Value.TypeSection.Value.Start, header.Value.TypeSection.Value.Size)
                        : (0, (ushort)0);
                    ReadOnlySpan<byte> typeSection = sectionSize == 0 ? new byte[] { 0, 0 } : container.Slice(startOffset, sectionSize);

                    valid &= ValidateSectionInstructions(sectionId, ref code, ref typeSection, header, spec);

                    if (spec.IsEip5450Enabled)
                    {

                        valid &= ValidateStackState(sectionId, ref code, ref typeSection, spec);
                    }

                }
                return valid;
            }
            else return true;
        }
        return false;
    }
    private bool ValidateStackState(ushort funcId, ref ReadOnlySpan<byte> code, ref ReadOnlySpan<byte> typesection, IReleaseSpec spec)
    {
        Dictionary<int, int?> visitedLines = new();
        int peakStackHeight = typesection[funcId * 2];

        Stack<(int Position, int StackHeigth)> workSet = new();
        workSet.Push((0, peakStackHeight));

        while (workSet.TryPop(out var worklet))
        {
            (int pos, int stackHeight) = worklet;
            bool stop = false;
            while (!stop)
            {
                Instruction opcode = (Instruction)code[pos];
                (var inputs, var immediates, var outputs) = opcode.StackRequirements(spec);

                if (visitedLines.ContainsKey(pos))
                {
                    if (stackHeight != visitedLines[pos])
                    {
                        if (LoggingEnabled)
                        {
                            _logger.Trace($"EIP-5450 : Branch joint line has invalid stack height");
                        }
                        return false;
                    }
                    break;
                }
                else
                {
                    visitedLines[pos] = stackHeight;
                }

                if (opcode is Instruction.CALLF or Instruction.JUMPF)
                {
                    var sectionIndex = code.Slice(pos + 1, 2).ReadEthUInt16();
                    inputs = typesection[sectionIndex * 2];
                    outputs = typesection[sectionIndex * 2 + 1];
                }

                if (stackHeight < inputs)
                {
                    if (LoggingEnabled)
                    {
                        _logger.Trace($"EIP-5450 : Stack Underflow required {inputs} but found {stackHeight}");
                    }
                    return false;
                }

                stackHeight += outputs - inputs;
                peakStackHeight = Math.Max(peakStackHeight, stackHeight);

                switch (opcode)
                {
                    case Instruction.RJUMP:
                        {
                            var offset = code.Slice(pos + 1, 2).ReadEthInt16();
                            var jumpDestination = pos + immediates + 1 + offset;
                            pos += jumpDestination;
                            break;
                        }
                    case Instruction.RJUMPI:
                        {
                            var offset = code.Slice(pos + 1, 2).ReadEthInt16();
                            var jumpDestination = pos + immediates + 1 + offset;
                            workSet.Push((jumpDestination, stackHeight));
                            pos += immediates + 1;
                            break;
                        }
                    case Instruction.RJUMPV:
                        {
                            var count = code[pos + 1];
                            immediates = count * 2 + 1;
                            for (short j = 0; j < count; j++)
                            {
                                int case_v = pos + 2 + j * 2;
                                int offset = code.Slice(case_v, 2).ReadEthInt16();
                                int jumptDestination = pos + immediates + 1 + offset;
                                workSet.Push((jumptDestination, stackHeight));
                            }
                            pos += immediates + 1;
                            break;
                        }
                    default:
                        {
                            if (opcode.IsTerminatingInstruction())
                            {
                                var expectedHeight = opcode is Instruction.RETF or Instruction.JUMPF ? typesection[funcId * 2 + 1] : 0;
                                if (expectedHeight != stackHeight)
                                {
                                    if (LoggingEnabled)
                                    {
                                        _logger.Trace($"EIP-5450 : Stack state invalid required height {expectedHeight} but found {stackHeight}");
                                    }
                                    return false;
                                }
                                stop = true;
                            }
                            else
                            {
                                pos += 1 + immediates;
                            }
                            break;
                        }
                }

            }

        }
        return peakStackHeight <= 1024;
    }

    public bool ValidateSectionInstructions(int sectionId, ref ReadOnlySpan<byte> codesection, ref ReadOnlySpan<byte> typesection, EofHeader? header, IReleaseSpec spec)
    {
        // check if code is EOF compliant
        if (!spec.IsEip3540Enabled)
        {
            return false;
        }

        if (!spec.IsEip3670Enabled)
        {
            return true;
        }

        Instruction? opcode = null;
        HashSet<Range> immediates = new HashSet<Range>();
        HashSet<Int32> rjumpdests = new HashSet<Int32>();
        for (int i = 0; i < codesection.Length;)
        {
            opcode = (Instruction)codesection[i];
            i++;
            // validate opcode
            if (!opcode.Value.IsValid(spec, true))
            {
                if (LoggingEnabled)
                {
                    _logger.Trace($"EIP-3670 : CodeSection contains undefined opcode {opcode}");
                }
                header = null; return false;
            }

            if (spec.IsEip4200Enabled)
            {
                if (opcode is Instruction.RJUMP or Instruction.RJUMPI)
                {
                    if (i + 2 > codesection.Length)
                    {
                        if (LoggingEnabled)
                        {
                            _logger.Trace($"EIP-4200 : Static Relative Jump Argument underflow");
                        }
                        header = null; return false;
                    }

                    var offset = codesection.Slice(i, 2).ReadEthInt16();
                    immediates.Add(new Range(i, i + 1));
                    var rjumpdest = offset + 2 + i;
                    rjumpdests.Add(rjumpdest);
                    if (rjumpdest < 0 || rjumpdest >= codesection.Length)
                    {
                        if (LoggingEnabled)
                        {
                            _logger.Trace($"EIP-4200 : Static Relative Jump Destination outside of Code bounds");
                        }
                        header = null; return false;
                    }
                    i += 2;
                }

                if (opcode is Instruction.RJUMPV)
                {
                    if (i + 2 > codesection.Length)
                    {
                        if (LoggingEnabled)
                        {
                            _logger.Trace($"EIP-4200 : Static Relative Jumpv Argument underflow");
                        }
                        header = null; return false;
                    }

                    byte count = codesection[i];
                    if (count < 1)
                    {
                        if (LoggingEnabled)
                        {
                            _logger.Trace($"EIP-4200 : jumpv jumptable must have at least 1 entry");
                        }
                        header = null; return false;
                    }
                    if (i + count * 2 > codesection.Length)
                    {
                        if (LoggingEnabled)
                        {
                            _logger.Trace($"EIP-4200 : jumpv jumptable underflow");
                        }
                        header = null; return false;
                    }
                    var immediateValueSize = 1 + count * 2;
                    immediates.Add(new Range(i, i + immediateValueSize - 1));
                    for (int j = 0; j < count; j++)
                    {
                        var offset = codesection.Slice(i + 1 + j * 2, 2).ReadEthInt16();
                        var rjumpdest = offset + immediateValueSize + i;
                        rjumpdests.Add(rjumpdest);
                        if (rjumpdest < 0 || rjumpdest >= codesection.Length)
                        {
                            if (LoggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : Static Relative Jumpv Destination outside of Code bounds");
                            }
                            header = null; return false;
                        }
                    }
                    i += immediateValueSize;
                }
            }

            if (spec.IsEip4750Enabled)
            {
                if (opcode is Instruction.CALLF)
                {
                    if (i + 2 > codesection.Length)
                    {
                        if (LoggingEnabled)
                        {
                            _logger.Trace($"EIP-4750 : CALLF Argument underflow");
                        }
                        header = null; return false;
                    }

                    var targetSectionId = codesection.Slice(i, 2).ReadEthUInt16();
                    immediates.Add(new Range(i, i + 1));

                    if (targetSectionId >= header.Value.CodeSections.Count)
                    {
                        if (LoggingEnabled)
                        {
                            _logger.Trace($"EIP-4750 : Invalid Section Id");
                        }
                        header = null; return false;
                    }
                    i += 2;
                }

                if (opcode is Instruction.JUMPF)
                {
                    if (i + 2 > codesection.Length)
                    {
                        if (LoggingEnabled)
                        {
                            _logger.Trace($"EIP-4750 : CALLF Argument underflow");
                        }
                        header = null; return false;
                    }

                    var targetSectionId = codesection.Slice(i, 2).ReadEthUInt16();
                    immediates.Add(new Range(i, i + 1));

                    if (targetSectionId >= header.Value.CodeSections.Count)
                    {
                        if (LoggingEnabled)
                        {
                            _logger.Trace($"EIP-4750 : Invalid Section Id");
                        }
                        header = null; return false;
                    }

                    if (typesection[targetSectionId * 2 + 1] != typesection[sectionId * 2 + 1])
                    {
                        if (LoggingEnabled)
                        {
                            _logger.Trace($"EIP-4750 : Incompatible Function Type for JUMPF");
                        }
                        header = null; return false;
                    }
                    i += 2;
                }
            }

            if (opcode is >= Instruction.PUSH1 and <= Instruction.PUSH32)
            {
                int len = codesection[i - 1] - (int)Instruction.PUSH1 + 1;
                immediates.Add(new Range(i, i + len - 1));
                i += len;
            }

            if (i > codesection.Length)
            {
                header = null; return false;
            }
        }

        if (spec.IsEip4750Enabled && !opcode.Value.IsTerminating(spec))
        {
            if (LoggingEnabled)
            {
                _logger.Trace($"EIP-4750 : Code Section must end with terminating opcode");
            }
            header = null; return false;
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
                        header = null; return false;
                    }
                }
            }
        }
        return true;
    }
}
