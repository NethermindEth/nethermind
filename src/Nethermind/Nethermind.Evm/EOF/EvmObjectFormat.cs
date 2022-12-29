using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;
using static System.Collections.Specialized.BitVector32;
using System.ComponentModel;
using System.Reflection.Emit;

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
            if (numberOfCodeSections < 1)
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

            if (typeSection.Size < 3)
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
                    header = null; return false;
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
                    header = null; return false;
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
            var codeSections = header.Value.CodeSections;
            var typeSections = header.Value.TypeSection.Size;
            if (codeSections.Length == 0 || codeSections.Any(section => section.Size == 0))
            {
                if (_loggingEnabled)
                {
                    _logger.Trace($"EIP-3540 : CodeSection size must follow a CodeSection, CodeSection length was {codeSections.Length}");
                }
                header = null; return false;
            }

            if (codeSections.Length > 1 && codeSections.Length != (typeSections / 4))
            {
                if (_loggingEnabled)
                {
                    _logger.Trace($"EIP-4750: Code Sections count must match TypeSection count, CodeSection count was {codeSections.Length}, expected {typeSections / 4}");
                }
                header = null; return false;
            }

            if (codeSections.Length > 1024)
            {
                if (_loggingEnabled)
                {
                    _logger.Trace($"EIP-4750 : Code section count limit exceeded only 1024 allowed but found {codeSections.Length}");
                }
                header = null; return false;
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
            for (int sectionId = 0; valid && sectionId < header.Value.CodeSections.Length; sectionId++)
            {
                var (typeSectionBegin, typeSectionSize) = header.Value.TypeSection;
                var (codeSectionBegin, codeSectionSize) = header.Value.CodeSections[sectionId];

                ReadOnlySpan<byte> code = container.Slice(codeSectionBegin, codeSectionSize);
                ReadOnlySpan<byte> typesection = container.Slice(typeSectionBegin, typeSectionSize);

                valid &= ValidateSectionInstructions(sectionId, in code, in typesection, ref header)
                      && ValidateStackState(sectionId, in code, in typesection, ref header);
            }
            return valid;
        }

        public bool ValidateSectionInstructions(int sectionId, in ReadOnlySpan<byte> code, in ReadOnlySpan<byte> typesection, ref EofHeader? header)
        {
            Instruction? opcode = null;

            HashSet<Range> immediates = new HashSet<Range>();
            HashSet<Int32> rjumpdests = new HashSet<Int32>();
            for (int i = 0; i < code.Length;)
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
                    header = null; return false;
                }

                if (_releaseSpec.StaticRelativeJumpsEnabled)
                {
                    if (opcode is Instruction.RJUMP or Instruction.RJUMPI)
                    {
                        if (i + 2 > code.Length)
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : Static Relative Jumpv Argument underflow");
                            }
                            header = null; return false;
                        }

                        var offset = code.Slice(i, 2).ReadEthInt16();
                        immediates.Add(new Range(i, i + 1));
                        var rjumpdest = offset + 2 + i;
                        rjumpdests.Add(rjumpdest);
                        if (rjumpdest < 0 || rjumpdest >= code.Length)
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : Static Relative Jump Destination outside of Code bounds");
                            }
                            header = null; return false;
                        }
                        i += 2;
                    }

                    if (opcode is Instruction.RJUMPV)
                    {
                        if (i + 2 > code.Length)
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : Static Relative Jumpv Argument underflow");
                            }
                            header = null; return false;
                        }

                        byte count = code[i];
                        if (count < 1)
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : jumpv jumptable must have at least 1 entry");
                            }
                            header = null; return false;
                        }
                        if (i + count * 2 > code.Length)
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-4200 : jumpv jumptable underflow");
                            }
                            header = null; return false;
                        }
                        var immediateValueSize = 1 + count * 2;
                        immediates.Add(new Range(i, i + immediateValueSize - 1));
                        for (int j = 0; j < count; j++)
                        {
                            var offset = code.Slice(i + 1 + j * 2, 2).ReadEthInt16();
                            var rjumpdest = offset + immediateValueSize + i;
                            rjumpdests.Add(rjumpdest);
                            if (rjumpdest < 0 || rjumpdest >= code.Length)
                            {
                                if (_loggingEnabled)
                                {
                                    _logger.Trace($"EIP-4200 : Static Relative Jumpv Destination outside of Code bounds");
                                }
                                header = null; return false;
                            }
                        }
                        i += immediateValueSize;
                    }
                }

                if (_releaseSpec.FunctionSections)
                {
                    if (opcode is Instruction.CALLF)
                    {
                        if (i + 2 > code.Length)
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-4750 : CALLF Argument underflow");
                            }
                            header = null; return false;
                        }

                        var targetSectionId = code.Slice(i, 2).ReadEthUInt16();
                        immediates.Add(new Range(i, i + 1));

                        if (targetSectionId >= header.Value.CodeSections.Length)
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-4750 : Invalid Section Id");
                            }
                            header = null; return false;
                        }
                        i += 2;
                    }
                }

                if (opcode is >= Instruction.PUSH1 and <= Instruction.PUSH32)
                {
                    int len = code[i - 1] - (int)Instruction.PUSH1 + 1;
                    immediates.Add(new Range(i, i + len - 1));
                    i += len;
                }

                if (i > code.Length)
                {
                    if (_loggingEnabled)
                    {
                        _logger.Trace($"EIP-3670 : PC Reached out of bounds");
                    }
                    header = null; return false;
                }
            }

            if (_releaseSpec.StaticRelativeJumpsEnabled)
            {

                foreach (int rjumpdest in rjumpdests)
                {
                    foreach (var range in immediates)
                    {
                        if (range.Includes(rjumpdest))
                        {
                            if (_loggingEnabled)
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
        public bool ValidateReachableCode(int sectionId, in ReadOnlySpan<byte> code, Dictionary<int, int>.KeyCollection reachedOpcode, in EofHeader? header)
        {
            for (int i = 0; i < code.Length;)
            {
                var opcode = (Instruction)code[i];

                if (!reachedOpcode.Contains(i))
                {
                    return false;
                }

                i++;
                if (opcode is Instruction.RJUMP or Instruction.RJUMPI or Instruction.CALLF)
                {
                    i += 2;
                }
                else if (opcode is Instruction.RJUMPV)
                {
                    byte count = code[i];

                    i += 1 + count * 2;
                }
                else if (opcode is >= Instruction.PUSH1 and <= Instruction.PUSH32)
                {
                    int len = code[i - 1] - (int)Instruction.PUSH1 + 1;
                    i += len;
                }
            }
            return true;
        }

        public bool ValidateStackState(int sectionId, in ReadOnlySpan<byte> code, in ReadOnlySpan<byte> typesection, ref EofHeader? header)
        {
            if (!_releaseSpec.IsEip5450Enabled)
            {
                return true;
            }

            Dictionary<int, int> recordedStackHeight = new();
            int peakStackHeight = typesection[sectionId * 4];
            ushort suggestedMaxHeight = typesection[(sectionId * 4 + 2)..(sectionId * 4 + 4)].ReadEthUInt16();

            Stack<(int Position, int StackHeigth)> workSet = new();
            workSet.Push((0, peakStackHeight));

            while (workSet.TryPop(out var worklet))
            {
                (int pos, int stackHeight) = worklet;
                bool stop = false;

                while (!stop)
                {
                    Instruction opcode = (Instruction)code[pos];
                    (var inputs, var immediates, var outputs) = opcode.StackRequirements(_releaseSpec);

                    if (recordedStackHeight.ContainsKey(pos))
                    {
                        if (stackHeight != recordedStackHeight[pos])
                        {
                            if (_loggingEnabled)
                            {
                                _logger.Trace($"EIP-5450 : Branch joint line has invalid stack height");
                            }
                            header = null; return false;
                        }
                        break;
                    }
                    else
                    {
                        recordedStackHeight[pos] = stackHeight;
                    }

                    if (opcode is Instruction.CALLF)
                    {
                        var sectionIndex = code.Slice(pos + 1, 2).ReadEthUInt16();
                        inputs = typesection[sectionIndex * 4];
                        outputs = typesection[sectionIndex * 4 + 1];
                    }

                    if (stackHeight < inputs)
                    {
                        if (_loggingEnabled)
                        {
                            _logger.Trace($"EIP-5450 : Stack Underflow required {inputs} but found {stackHeight}");
                        }
                        header = null; return false;
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
                                if (opcode.IsTerminating(_releaseSpec))
                                {
                                    var expectedHeight = opcode is Instruction.RETF ? typesection[sectionId * 4 + 1] : 0;
                                    if (expectedHeight != stackHeight)
                                    {
                                        if (_loggingEnabled)
                                        {
                                            _logger.Trace($"EIP-5450 : Stack state invalid required height {expectedHeight} but found {stackHeight}");
                                        }
                                        header = null; return false;
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

            if (!ValidateReachableCode(sectionId, code, recordedStackHeight.Keys, in header))
            {
                if (_loggingEnabled)
                {
                    _logger.Trace($"EIP-5450 : bytecode has unreachable segments");
                }
                header = null; return false;
            }

            if (peakStackHeight != suggestedMaxHeight)
            {
                if (_loggingEnabled)
                {
                    _logger.Trace($"EIP-5450 : Suggested Max Stack height mismatches with actual Max, expected {suggestedMaxHeight} but found {peakStackHeight}");
                }
                header = null; return false;
            }

            return peakStackHeight < 1024;
        }
    }
}
