// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DotNetty.Common.Utilities;
using FastEnumUtility;
using Nethermind.Core.Extensions;
using Nethermind.Evm;

using static Nethermind.Evm.EvmObjectFormat.EofValidator;

namespace Nethermind.Evm.EvmObjectFormat.Handlers;

internal class Eof1 : IEofVersionHandler
{
    private readonly struct QueueManager
    {
        public readonly Queue<(int index, ValidationStrategy strategy)> ContainerQueue;
        public readonly ValidationStrategy[] VisitedContainers;

        public QueueManager(int containerCount)
        {
            ContainerQueue = new();
            VisitedContainers = new ValidationStrategy[containerCount];
        }

        public void Enqueue(int index, ValidationStrategy strategy)
        {
            ContainerQueue.Enqueue((index, strategy));
        }

        public void MarkVisited(int index, ValidationStrategy strategy)
        {
            VisitedContainers[index] = strategy;
        }

        public bool TryDequeue(out (int Index, ValidationStrategy Strategy) worklet) => ContainerQueue.TryDequeue(out worklet);

        public bool IsAllVisited() => VisitedContainers.All(x => x != 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StackBounds()
    {
        public short Max = -1;
        public short Min = 1023;

        public void Combine(StackBounds other)
        {
            this.Max = Math.Max(this.Max, other.Max);
            this.Min = Math.Min(this.Min, other.Min);
        }

        public bool BoundsEqual() => Max == Min;

        public static bool operator ==(StackBounds left, StackBounds right) => left.Max == right.Max && right.Min == left.Min;
        public static bool operator !=(StackBounds left, StackBounds right) => !(left == right);
        public override bool Equals(object obj) => obj is StackBounds && this == (StackBounds)obj;
        public override int GetHashCode() => Max ^ Min;
    }

    private ref struct Sizes
    {
        public ushort? TypeSectionSize;
        public ushort? CodeSectionSize;
        public ushort? DataSectionSize;
        public ushort? ContainerSectionSize;
    }

    public const byte VERSION = 0x01;
    internal enum Separator : byte
    {
        KIND_TYPE = 0x01,
        KIND_CODE = 0x02,
        KIND_CONTAINER = 0x03,
        KIND_DATA = 0x04,
        TERMINATOR = 0x00
    }

    internal const byte MINIMUM_HEADER_SECTION_SIZE = 3;
    internal const byte MINIMUM_TYPESECTION_SIZE = 4;
    internal const byte MINIMUM_CODESECTION_SIZE = 1;
    internal const byte MINIMUM_DATASECTION_SIZE = 0;
    internal const byte MINIMUM_CONTAINERSECTION_SIZE = 0;
    internal const byte MINIMUM_HEADER_SIZE = EofValidator.VERSION_OFFSET
                                            + MINIMUM_HEADER_SECTION_SIZE
                                            + MINIMUM_HEADER_SECTION_SIZE + EofValidator.TWO_BYTE_LENGTH
                                            + MINIMUM_HEADER_SECTION_SIZE
                                            + EofValidator.ONE_BYTE_LENGTH;

    internal const byte BYTE_BIT_COUNT = 8; // indicates the length of the count immediate of jumpv
    internal const byte MINIMUMS_ACCEPTABLE_JUMPV_JUMPTABLE_LENGTH = 1; // indicates the length of the count immediate of jumpv

    internal const byte INPUTS_OFFSET = 0;
    internal const byte INPUTS_MAX = 0x7F;

    internal const byte OUTPUTS_OFFSET = INPUTS_OFFSET + 1;
    internal const byte OUTPUTS_MAX = 0x7F;
    internal const byte NON_RETURNING = 0x80;

    internal const byte MAX_STACK_HEIGHT_OFFSET = OUTPUTS_OFFSET + 1;
    internal const int MAX_STACK_HEIGHT_LENGTH = 2;
    internal const ushort MAX_STACK_HEIGHT = 0x400;

    internal const ushort MINIMUM_NUM_CODE_SECTIONS = 1;
    internal const ushort MAXIMUM_NUM_CODE_SECTIONS = 1024;
    internal const ushort MAXIMUM_NUM_CONTAINER_SECTIONS = 0x00FF;
    internal const ushort RETURN_STACK_MAX_HEIGHT = MAXIMUM_NUM_CODE_SECTIONS; // the size in the type sectionn allocated to each function section

    internal const ushort MINIMUM_SIZE = MINIMUM_HEADER_SIZE
                                        + MINIMUM_TYPESECTION_SIZE // minimum type section body size
                                        + MINIMUM_CODESECTION_SIZE // minimum code section body size
                                        + MINIMUM_DATASECTION_SIZE; // minimum data section body size

    // EIP-3540 ties this to MAX_INIT_CODE_SIZE from EIP-3860, but we need a constant here
    internal const ushort MAXIMUM_SIZE = 0xc000;

    public bool TryParseEofHeader(ReadOnlyMemory<byte> containerMemory, ValidationStrategy validationStrategy, out EofHeader? header)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ushort GetUInt16(ReadOnlySpan<byte> container, int offset) =>
            container.Slice(offset, EofValidator.TWO_BYTE_LENGTH).ReadEthUInt16();

        ReadOnlySpan<byte> container = containerMemory.Span;

        header = null;
        // we need to be able to parse header + minimum section lengths
        if (container.Length < MINIMUM_SIZE)
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
            return false;
        }
        if (container.Length > MAXIMUM_SIZE)
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is larger than allowed maximum size of {MAXIMUM_SIZE}");
            return false;
        }

        if (!container.StartsWith(EofValidator.MAGIC))
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code doesn't start with magic byte sequence expected {EofValidator.MAGIC.ToHexString(true)} ");
            return false;
        }

        if (container[EofValidator.VERSION_OFFSET] != VERSION)
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is not Eof version {VERSION}");
            return false;
        }

        Sizes sectionSizes = new();
        int[] codeSections = null;
        int[] containerSections = null;
        int pos = EofValidator.VERSION_OFFSET + 1;

        var continueParsing = true;
        while (continueParsing && pos < container.Length)
        {
            var separator = (Separator)container[pos++];

            switch (separator)
            {
                case Separator.KIND_TYPE:
                    if (sectionSizes.TypeSectionSize != null)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Multiple type sections");
                        return false;
                    }

                    if (container.Length < pos + EofValidator.TWO_BYTE_LENGTH)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                        return false;
                    }

                    sectionSizes.TypeSectionSize = GetUInt16(container, pos);
                    if (sectionSizes.TypeSectionSize < MINIMUM_TYPESECTION_SIZE)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, TypeSection Size must be at least 3, but found {sectionSizes.TypeSectionSize}");
                        return false;
                    }

                    pos += EofValidator.TWO_BYTE_LENGTH;
                    break;
                case Separator.KIND_CODE:
                    if (sectionSizes.CodeSectionSize != null)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Multiple code sections");
                        return false;
                    }

                    if (sectionSizes.TypeSectionSize is null)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is not well formatted");
                        return false;
                    }

                    if (container.Length < pos + EofValidator.TWO_BYTE_LENGTH)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                        return false;
                    }

                    var numberOfCodeSections = GetUInt16(container, pos);
                    sectionSizes.CodeSectionSize = (ushort)(numberOfCodeSections * EofValidator.TWO_BYTE_LENGTH);
                    if (numberOfCodeSections > MAXIMUM_NUM_CODE_SECTIONS)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, code sections count must not exceed {MAXIMUM_NUM_CODE_SECTIONS}");
                        return false;
                    }

                    if (container.Length < pos + EofValidator.TWO_BYTE_LENGTH + EofValidator.TWO_BYTE_LENGTH * numberOfCodeSections)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                        return false;
                    }

                    codeSections = new int[numberOfCodeSections];
                    int CODESECTION_HEADER_PREFIX_SIZE = pos + EofValidator.TWO_BYTE_LENGTH;
                    for (ushort i = 0; i < codeSections.Length; i++)
                    {
                        int currentCodeSizeOffset = CODESECTION_HEADER_PREFIX_SIZE + i * EofValidator.TWO_BYTE_LENGTH; // offset of pos'th code size
                        int codeSectionSize = GetUInt16(container, currentCodeSizeOffset);

                        if (codeSectionSize == 0)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Empty Code Section are not allowed, CodeSectionSize must be > 0 but found {codeSectionSize}");
                            return false;
                        }

                        codeSections[i] = codeSectionSize;
                    }

                    pos += EofValidator.TWO_BYTE_LENGTH + EofValidator.TWO_BYTE_LENGTH * codeSections.Length;
                    break;
                case Separator.KIND_CONTAINER:
                    if (sectionSizes.ContainerSectionSize != null)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Multiple container sections");
                        return false;
                    }

                    if (sectionSizes.CodeSectionSize is null)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is not well formatted");
                        return false;
                    }

                    if (sectionSizes.DataSectionSize is not null)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Container section is out of order");
                        return false;
                    }

                    if (container.Length < pos + EofValidator.TWO_BYTE_LENGTH)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                        return false;
                    }

                    var numberOfContainerSections = GetUInt16(container, pos);
                    sectionSizes.ContainerSectionSize = (ushort)(numberOfContainerSections * EofValidator.TWO_BYTE_LENGTH);
                    if (numberOfContainerSections is > (MAXIMUM_NUM_CONTAINER_SECTIONS + 1) or 0)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, container sections count must not exceed {MAXIMUM_NUM_CONTAINER_SECTIONS}");
                        return false;
                    }

                    if (container.Length < pos + EofValidator.TWO_BYTE_LENGTH + EofValidator.TWO_BYTE_LENGTH * numberOfContainerSections)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                        return false;
                    }

                    containerSections = new int[numberOfContainerSections];
                    int CONTAINER_SECTION_HEADER_PREFIX_SIZE = pos + EofValidator.TWO_BYTE_LENGTH;
                    for (ushort i = 0; i < containerSections.Length; i++)
                    {
                        int currentContainerSizeOffset = CONTAINER_SECTION_HEADER_PREFIX_SIZE + i * EofValidator.TWO_BYTE_LENGTH; // offset of pos'th code size
                        int containerSectionSize = GetUInt16(container, currentContainerSizeOffset);

                        if (containerSectionSize == 0)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Empty Container Section are not allowed, containerSectionSize must be > 0 but found {containerSectionSize}");
                            return false;
                        }

                        containerSections[i] = containerSectionSize;
                    }

                    pos += EofValidator.TWO_BYTE_LENGTH + EofValidator.TWO_BYTE_LENGTH * containerSections.Length;
                    break;
                case Separator.KIND_DATA:
                    if (sectionSizes.DataSectionSize != null)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Multiple data sections");
                        return false;
                    }

                    if (sectionSizes.CodeSectionSize is null)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is not well fromated");
                        return false;
                    }

                    if (container.Length < pos + EofValidator.TWO_BYTE_LENGTH)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                        return false;
                    }

                    sectionSizes.DataSectionSize = GetUInt16(container, pos);

                    pos += EofValidator.TWO_BYTE_LENGTH;
                    break;
                case Separator.TERMINATOR:
                    if (container.Length < pos + EofValidator.ONE_BYTE_LENGTH)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                        return false;
                    }

                    continueParsing = false;
                    break;
                default:
                    if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code header is not well formatted");
                    return false;
            }
        }

        if (sectionSizes.TypeSectionSize is null || sectionSizes.CodeSectionSize is null || sectionSizes.DataSectionSize is null)
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code is not well formatted");
            return false;
        }

        var typeSectionSubHeader = new SectionHeader(pos, sectionSizes.TypeSectionSize.Value);
        var codeSectionSubHeader = new CompoundSectionHeader(typeSectionSubHeader.EndOffset, codeSections);
        CompoundSectionHeader? containerSectionSubHeader = containerSections is null ? null
            : new CompoundSectionHeader(codeSectionSubHeader.EndOffset, containerSections);
        var dataSectionSubHeader = new SectionHeader(containerSectionSubHeader?.EndOffset ?? codeSectionSubHeader.EndOffset, sectionSizes.DataSectionSize.Value);

        if (dataSectionSubHeader.EndOffset < containerMemory.Length)
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Extra data after end of container, starting at {dataSectionSubHeader.EndOffset}");
            return false;
        }
        if ((validationStrategy.HasFlag(ValidationStrategy.Validate) && !validationStrategy.HasFlag(ValidationStrategy.ValidateRuntimeMode))
            && dataSectionSubHeader.EndOffset > container.Length)
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Container has truncated data where full data is required");
            return false;
        }

        header = new EofHeader
        {
            Version = VERSION,
            PrefixSize = pos,
            TypeSection = typeSectionSubHeader,
            CodeSections = codeSectionSubHeader,
            ContainerSections = containerSectionSubHeader,
            DataSection = dataSectionSubHeader,
        };
        return true;
    }

    public bool TryGetEofContainer(ReadOnlyMemory<byte> code, ValidationStrategy validationStrategy, [NotNullWhen(true)] out EofContainer? eofContainer)
    {
        if (!TryParseEofHeader(code, validationStrategy, out EofHeader? header))
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Header not parsed");
            eofContainer = null;
            return false;
        }

        if (!ValidateBody(code.Span, header.Value, validationStrategy))
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Body not valid");
            eofContainer = null;
            return false;
        }

        eofContainer = new EofContainer(code, header.Value);

        if (validationStrategy.HasFlag(ValidationStrategy.Validate))
        {
            if (!ValidateContainer(eofContainer.Value, validationStrategy))
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Container not valid");
                eofContainer = null;
                return false;
            }
        }

        return true;
    }

    private bool ValidateContainer(EofContainer eofContainer, ValidationStrategy validationStrategy)
    {
        Queue<(EofContainer container, ValidationStrategy strategy)> containers = new();
        containers.Enqueue((eofContainer, validationStrategy));
        while (containers.TryDequeue(out var target))
        {
            EofContainer targetContainer = target.container;
            validationStrategy = target.strategy;

            QueueManager containerQueue = new(1 + (targetContainer.Header.ContainerSections?.Count ?? 0));
            containerQueue.Enqueue(0, validationStrategy);

            containerQueue.VisitedContainers[0] = GetValidation(validationStrategy);

            while (containerQueue.TryDequeue(out var worklet))
            {
                if (worklet.Index != 0)
                {
                    if (containerQueue.VisitedContainers[worklet.Index] != 0)
                        continue;

                    if (targetContainer.ContainerSections.Length < worklet.Index)
                        continue;

                    var section = worklet.Index - 1;
                    ReadOnlyMemory<byte> subsection = targetContainer.ContainerSections[section];
                    if (!TryParseEofHeader(subsection, validationStrategy, out EofHeader? header))
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Header invalid: section {section}");
                        return false;
                    }
                    if (!ValidateBody(subsection.Span, header.Value, validationStrategy))
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Body invalid: section {section}");
                        return false;
                    }

                    if (validationStrategy.HasFlag(ValidationStrategy.Validate))
                    {
                        // Clear the Initcode flag for subcontainer
                        ValidationStrategy subContainerValidation = validationStrategy & ~ValidationStrategy.ValidateInitcodeMode;
                        // Set the Runtime flag for subcontainer
                        subContainerValidation |= ValidationStrategy.ValidateRuntimeMode;
                        containers.Enqueue((new EofContainer(subsection, header.Value), subContainerValidation));
                    }
                }
                else
                {
                    if (!ValidateCodeSections(targetContainer, worklet.Strategy, in containerQueue))
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code sections invalid");
                        return false;
                    }
                }
                containerQueue.MarkVisited(worklet.Index, GetVisited(worklet.Strategy));
            }
            if (!containerQueue.IsAllVisited())
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Not all containers visited");
                return false;
            }
        }

        return true;
    }

    private static ValidationStrategy GetVisited(ValidationStrategy validationStrategy)
    {
        return validationStrategy.HasFlag(ValidationStrategy.ValidateInitcodeMode)
            ? ValidationStrategy.ValidateInitcodeMode
            : ValidationStrategy.ValidateRuntimeMode;
    }

    private static ValidationStrategy GetValidation(ValidationStrategy validationStrategy)
    {
        return validationStrategy.HasFlag(ValidationStrategy.ValidateInitcodeMode)
            ? ValidationStrategy.ValidateInitcodeMode
            : validationStrategy.HasFlag(ValidationStrategy.ValidateRuntimeMode)
                ? ValidationStrategy.ValidateRuntimeMode
                : ValidationStrategy.None;
    }

    private bool ValidateBody(ReadOnlySpan<byte> container, EofHeader header, ValidationStrategy strategy)
    {
        int startOffset = header.TypeSection.Start;
        int endOffset = header.DataSection.Start;
        int calculatedCodeLength =
                header.TypeSection.Size
            + header.CodeSections.Size
            + (header.ContainerSections?.Size ?? 0);
        CompoundSectionHeader codeSections = header.CodeSections;

        if (endOffset > container.Length)
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, DataSectionSize indicated in bundled header are incorrect, or DataSection is wrong");
            return false;
        }

        ReadOnlySpan<byte> contractBody = container[startOffset..endOffset];
        ReadOnlySpan<byte> dataBody = container[endOffset..];
        var typeSection = header.TypeSection;
        (int typeSectionStart, int typeSectionSize) = (typeSection.Start, typeSection.Size);

        if (header.ContainerSections?.Count > MAXIMUM_NUM_CONTAINER_SECTIONS + 1)
        {
            // move this check where `header.ExtraContainers.Count` is parsed
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, initcode Containers count must be less than {MAXIMUM_NUM_CONTAINER_SECTIONS} but found {header.ContainerSections?.Count}");
            return false;
        }

        if (contractBody.Length != calculatedCodeLength)
        {
            if (Logger.IsTrace) Logger.Trace("EOF: Eof{VERSION}, SectionSizes indicated in bundled header are incorrect, or ContainerCode is incomplete");
            return false;
        }

        if (strategy.HasFlag(ValidationStrategy.ValidateFullBody) && header.DataSection.Size > dataBody.Length)
        {
            if (Logger.IsTrace) Logger.Trace("EOF: Eof{VERSION}, DataSectionSize indicated in bundled header are incorrect, or DataSection is wrong");
            return false;
        }

        if (!strategy.HasFlag(ValidationStrategy.AllowTrailingBytes) && strategy.HasFlag(ValidationStrategy.ValidateFullBody) && header.DataSection.Size != dataBody.Length)
        {
            if (Logger.IsTrace) Logger.Trace("EOF: Eof{VERSION}, DataSectionSize indicated in bundled header are incorrect, or DataSection is wrong");
            return false;
        }

        if (codeSections.Count == 0 || codeSections.SubSectionsSizes.Any(size => size == 0))
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, CodeSection size must follow a CodeSection, CodeSection length was {codeSections.Count}");
            return false;
        }

        if (codeSections.Count != typeSectionSize / MINIMUM_TYPESECTION_SIZE)
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code Sections count must match TypeSection count, CodeSection count was {codeSections.Count}, expected {typeSectionSize / MINIMUM_TYPESECTION_SIZE}");
            return false;
        }

        ReadOnlySpan<byte> typesection = container.Slice(typeSectionStart, typeSectionSize);
        if (!ValidateTypeSection(typesection))
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, invalid typesection found");
            return false;
        }

        return true;
    }

    private bool ValidateCodeSections(EofContainer eofContainer, ValidationStrategy strategy, in QueueManager containerQueue)
    {
        QueueManager sectionQueue = new(eofContainer.Header.CodeSections.Count);

        sectionQueue.Enqueue(0, strategy);

        while (sectionQueue.TryDequeue(out var sectionIdx))
        {
            if (sectionQueue.VisitedContainers[sectionIdx.Index] != 0)
                continue;

            if (!ValidateInstructions(eofContainer, sectionIdx.Index, strategy, in sectionQueue, in containerQueue))
                return false;

            sectionQueue.MarkVisited(sectionIdx.Index, ValidationStrategy.Validate);
        }

        return sectionQueue.IsAllVisited();
    }

    private bool ValidateTypeSection(ReadOnlySpan<byte> types)
    {
        if (types[INPUTS_OFFSET] != 0 || types[OUTPUTS_OFFSET] != NON_RETURNING)
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, first 2 bytes of type section must be 0s");
            return false;
        }

        if (types.Length % MINIMUM_TYPESECTION_SIZE != 0)
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, type section length must be a product of {MINIMUM_TYPESECTION_SIZE}");
            return false;
        }

        for (var offset = 0; offset < types.Length; offset += MINIMUM_TYPESECTION_SIZE)
        {
            var inputCount = types[offset + INPUTS_OFFSET];
            var outputCount = types[offset + OUTPUTS_OFFSET];
            ushort maxStackHeight = types.Slice(offset + MAX_STACK_HEIGHT_OFFSET, MAX_STACK_HEIGHT_LENGTH).ReadEthUInt16();

            if (inputCount > INPUTS_MAX)
            {
                if (Logger.IsTrace) Logger.Trace("EOF: Eof{VERSION}, Too many inputs");
                return false;
            }

            if (outputCount > OUTPUTS_MAX && outputCount != NON_RETURNING)
            {
                if (Logger.IsTrace) Logger.Trace("EOF: Eof{VERSION}, Too many outputs");
                return false;
            }

            if (maxStackHeight > MAX_STACK_HEIGHT)
            {
                if (Logger.IsTrace) Logger.Trace("EOF: Eof{VERSION}, Stack depth too high");
                return false;
            }
        }
        return true;
    }

    private bool ValidateInstructions(EofContainer eofContainer, int sectionId, ValidationStrategy strategy, in QueueManager sectionsWorklist, in QueueManager containersWorklist)
    {
        ReadOnlySpan<byte> code = eofContainer.CodeSections[sectionId].Span;

        if (code.Length < 1)
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, CodeSection {sectionId} is too short to be valid");
            return false;
        }

        var length = code.Length / BYTE_BIT_COUNT + 1;
        byte[] invalidJmpLocationArray = ArrayPool<byte>.Shared.Rent(length);
        byte[] jumpDestinationsArray = ArrayPool<byte>.Shared.Rent(length);

        try
        {
            // ArrayPool may return a larger array than requested, so we need to slice it to the actual length
            Span<byte> invalidJumpDestinations = invalidJmpLocationArray.AsSpan(0, length);
            Span<byte> jumpDestinations = jumpDestinationsArray.AsSpan(0, length);
            // ArrayPool may return a larger array than requested, so we need to slice it to the actual length
            invalidJumpDestinations.Clear();
            jumpDestinations.Clear();

            ReadOnlySpan<byte> currentTypeSection = eofContainer.TypeSections[sectionId].Span;
            var isCurrentSectionNonReturning = currentTypeSection[OUTPUTS_OFFSET] == 0x80;

            int position;
            Instruction opcode = Instruction.STOP;
            for (position = 0; position < code.Length;)
            {
                opcode = (Instruction)code[position];
                int nextPosition = position + 1;

                if (!opcode.IsValid(IsEofContext: true))
                {
                    if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, CodeSection contains undefined opcode {opcode}");
                    return false;
                }
                else if (opcode is Instruction.RETURN or Instruction.STOP)
                {
                    if (strategy.HasFlag(ValidationStrategy.ValidateInitcodeMode))
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, CodeSection contains {opcode} opcode");
                        return false;
                    }
                    else
                    {
                        if (containersWorklist.VisitedContainers[0] == ValidationStrategy.ValidateInitcodeMode)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, CodeSection cannot contain {opcode} opcode");
                            return false;
                        }
                        else
                        {
                            containersWorklist.VisitedContainers[0] = ValidationStrategy.ValidateRuntimeMode;
                        }
                    }
                }
                else if (opcode is Instruction.RETURNCONTRACT)
                {
                    if (strategy.HasFlag(ValidationStrategy.ValidateRuntimeMode))
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, CodeSection contains {opcode} opcode");
                        return false;
                    }
                    else
                    {
                        if (containersWorklist.VisitedContainers[0] == ValidationStrategy.ValidateRuntimeMode)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, CodeSection cannot contain {opcode} opcode");
                            return false;
                        }
                        else
                        {
                            containersWorklist.VisitedContainers[0] = ValidationStrategy.ValidateInitcodeMode;
                        }
                    }

                    if (nextPosition + EofValidator.ONE_BYTE_LENGTH > code.Length)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RETURNCONTRACT} Argument underflow");
                        return false;
                    }

                    ushort runtimeContainerId = code[nextPosition];
                    if (eofContainer.Header.ContainerSections is null || runtimeContainerId >= eofContainer.Header.ContainerSections?.Count)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RETURNCONTRACT}'s immediate argument must be less than containerSection.Count i.e: {eofContainer.Header.ContainerSections?.Count}");
                        return false;
                    }

                    if (containersWorklist.VisitedContainers[runtimeContainerId + 1] != 0
                        && containersWorklist.VisitedContainers[runtimeContainerId + 1] != ValidationStrategy.ValidateRuntimeMode)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RETURNCONTRACT}'s target container can only be a runtime mode bytecode");
                        return false;
                    }

                    containersWorklist.Enqueue(runtimeContainerId + 1, ValidationStrategy.ValidateRuntimeMode);

                    BitmapHelper.HandleNumbits(EofValidator.ONE_BYTE_LENGTH, invalidJumpDestinations, ref nextPosition);
                }
                else if (opcode is Instruction.RJUMP or Instruction.RJUMPI)
                {
                    if (nextPosition + EofValidator.TWO_BYTE_LENGTH > code.Length)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {opcode.FastToString()} Argument underflow");
                        return false;
                    }

                    short offset = code.Slice(nextPosition, EofValidator.TWO_BYTE_LENGTH).ReadEthInt16();
                    int rjumpDest = offset + EofValidator.TWO_BYTE_LENGTH + nextPosition;

                    if (rjumpDest < 0 || rjumpDest >= code.Length)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {opcode.FastToString()} Destination outside of Code bounds");
                        return false;
                    }

                    BitmapHelper.HandleNumbits(EofValidator.ONE_BYTE_LENGTH, jumpDestinations, ref rjumpDest);
                    BitmapHelper.HandleNumbits(EofValidator.TWO_BYTE_LENGTH, invalidJumpDestinations, ref nextPosition);
                }
                else if (opcode is Instruction.JUMPF)
                {
                    if (nextPosition + EofValidator.TWO_BYTE_LENGTH > code.Length)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.JUMPF} Argument underflow");
                        return false;
                    }

                    var targetSectionId = code.Slice(nextPosition, EofValidator.TWO_BYTE_LENGTH).ReadEthUInt16();

                    if (targetSectionId >= eofContainer.Header.CodeSections.Count)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.JUMPF} to unknown code section");
                        return false;
                    }

                    ReadOnlySpan<byte> targetTypeSection = eofContainer.TypeSections[targetSectionId].Span;

                    var targetSectionOutputCount = targetTypeSection[OUTPUTS_OFFSET];
                    var isTargetSectionNonReturning = targetTypeSection[OUTPUTS_OFFSET] == 0x80;
                    var currentSectionOutputCount = currentTypeSection[OUTPUTS_OFFSET];

                    if (!isTargetSectionNonReturning && currentSectionOutputCount < targetSectionOutputCount)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.JUMPF} to code section with more outputs");
                        return false;
                    }

                    sectionsWorklist.Enqueue(targetSectionId, strategy);
                    BitmapHelper.HandleNumbits(EofValidator.TWO_BYTE_LENGTH, invalidJumpDestinations, ref nextPosition);
                }
                else if (opcode is Instruction.DUPN or Instruction.SWAPN or Instruction.EXCHANGE)
                {
                    if (nextPosition + EofValidator.ONE_BYTE_LENGTH > code.Length)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {opcode.FastToString()} Argument underflow");
                        return false;
                    }
                    BitmapHelper.HandleNumbits(EofValidator.ONE_BYTE_LENGTH, invalidJumpDestinations, ref nextPosition);
                }
                else if (opcode is Instruction.RJUMPV)
                {
                    if (nextPosition + EofValidator.ONE_BYTE_LENGTH + EofValidator.TWO_BYTE_LENGTH > code.Length)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RJUMPV} Argument underflow");
                        return false;
                    }

                    var count = (ushort)(code[nextPosition] + 1);
                    if (count < MINIMUMS_ACCEPTABLE_JUMPV_JUMPTABLE_LENGTH)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RJUMPV} jumpTable must have at least 1 entry");
                        return false;
                    }

                    if (nextPosition + EofValidator.ONE_BYTE_LENGTH + count * EofValidator.TWO_BYTE_LENGTH > code.Length)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RJUMPV} jumpTable underflow");
                        return false;
                    }

                    int immediateValueSize = EofValidator.ONE_BYTE_LENGTH + count * EofValidator.TWO_BYTE_LENGTH;
                    for (var j = 0; j < count; j++)
                    {
                        var offset = code.Slice(nextPosition + EofValidator.ONE_BYTE_LENGTH + j * EofValidator.TWO_BYTE_LENGTH, EofValidator.TWO_BYTE_LENGTH).ReadEthInt16();
                        var rjumpDest = offset + immediateValueSize + nextPosition;
                        if (rjumpDest < 0 || rjumpDest >= code.Length)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RJUMPV} Destination outside of Code bounds");
                            return false;
                        }
                        BitmapHelper.HandleNumbits(EofValidator.ONE_BYTE_LENGTH, jumpDestinations, ref rjumpDest);
                    }

                    BitmapHelper.HandleNumbits(immediateValueSize, invalidJumpDestinations, ref nextPosition);
                }
                else if (opcode is Instruction.CALLF)
                {
                    if (nextPosition + EofValidator.TWO_BYTE_LENGTH > code.Length)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.CALLF} Argument underflow");
                        return false;
                    }

                    ushort targetSectionId = code.Slice(nextPosition, EofValidator.TWO_BYTE_LENGTH).ReadEthUInt16();

                    if (targetSectionId >= eofContainer.Header.CodeSections.Count)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.CALLF} Invalid Section Id");
                        return false;
                    }

                    ReadOnlySpan<byte> targetTypeSection = eofContainer.TypeSections[targetSectionId].Span;

                    var targetSectionOutputCount = targetTypeSection[OUTPUTS_OFFSET];

                    if (targetSectionOutputCount == 0x80)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.CALLF} into non-returning function");
                        return false;
                    }

                    sectionsWorklist.Enqueue(targetSectionId, strategy);
                    BitmapHelper.HandleNumbits(EofValidator.TWO_BYTE_LENGTH, invalidJumpDestinations, ref nextPosition);
                }
                else if (opcode is Instruction.RETF && isCurrentSectionNonReturning)
                {
                    if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, non returning sections are not allowed to use opcode {Instruction.RETF}");
                    return false;
                }
                else if (opcode is Instruction.DATALOADN)
                {
                    if (nextPosition + EofValidator.TWO_BYTE_LENGTH > code.Length)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.DATALOADN} Argument underflow");
                        return false;
                    }

                    ushort dataSectionOffset = code.Slice(nextPosition, EofValidator.TWO_BYTE_LENGTH).ReadEthUInt16();

                    if (dataSectionOffset + 32 > eofContainer.Header.DataSection.Size)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.DATALOADN}'s immediate argument must be less than dataSection.Length / 32 i.e: {eofContainer.Header.DataSection.Size / 32}");
                        return false;
                    }
                    BitmapHelper.HandleNumbits(EofValidator.TWO_BYTE_LENGTH, invalidJumpDestinations, ref nextPosition);
                }
                else if (opcode is Instruction.EOFCREATE)
                {
                    if (nextPosition + EofValidator.ONE_BYTE_LENGTH > code.Length)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.EOFCREATE} Argument underflow");
                        return false;
                    }

                    int initCodeSectionId = code[nextPosition];

                    if (eofContainer.Header.ContainerSections is null || initCodeSectionId >= eofContainer.Header.ContainerSections?.Count)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.EOFCREATE}'s immediate must falls within the Containers' range available, i.e: {eofContainer.Header.CodeSections.Count}");
                        return false;
                    }

                    if (containersWorklist.VisitedContainers[initCodeSectionId + 1] != 0
                        && containersWorklist.VisitedContainers[initCodeSectionId + 1] != ValidationStrategy.ValidateInitcodeMode)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.EOFCREATE}'s target container can only be a initCode mode bytecode");
                        return false;
                    }

                    containersWorklist.Enqueue(initCodeSectionId + 1, ValidationStrategy.ValidateInitcodeMode | ValidationStrategy.ValidateFullBody);

                    BitmapHelper.HandleNumbits(EofValidator.ONE_BYTE_LENGTH, invalidJumpDestinations, ref nextPosition);
                }
                else if (opcode is >= Instruction.PUSH0 and <= Instruction.PUSH32)
                {
                    int pushDataLength = opcode - Instruction.PUSH0;
                    if (nextPosition + pushDataLength > code.Length)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {opcode.FastToString()} PC Reached out of bounds");
                        return false;
                    }
                    BitmapHelper.HandleNumbits(pushDataLength, invalidJumpDestinations, ref nextPosition);
                }
                position = nextPosition;
            }

            if (position > code.Length)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, PC Reached out of bounds");
                return false;
            }

            if (!opcode.IsTerminating())
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code section {sectionId} ends with a non-terminating opcode");
                return false;
            }

            var result = BitmapHelper.CheckCollision(invalidJumpDestinations, jumpDestinations);
            if (result)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Invalid Jump destination {result}");
                return false;
            }

            if (!ValidateStackState(sectionId, code, eofContainer.TypeSection.Span))
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Invalid Stack state");
                return false;
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(invalidJmpLocationArray);
            ArrayPool<byte>.Shared.Return(jumpDestinationsArray);
        }
    }
    public bool ValidateStackState(int sectionId, in ReadOnlySpan<byte> code, in ReadOnlySpan<byte> typesection)
    {
        StackBounds[] recordedStackHeight = ArrayPool<StackBounds>.Shared.Rent(code.Length);
        recordedStackHeight.Fill(new StackBounds());

        try
        {
            ushort suggestedMaxHeight = typesection.Slice(sectionId * MINIMUM_TYPESECTION_SIZE + EofValidator.TWO_BYTE_LENGTH, EofValidator.TWO_BYTE_LENGTH).ReadEthUInt16();

            var currrentSectionOutputs = typesection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET] == 0x80 ? (ushort)0 : typesection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
            short peakStackHeight = typesection[sectionId * MINIMUM_TYPESECTION_SIZE + INPUTS_OFFSET];

            var unreachedBytes = code.Length;
            var isTargetSectionNonReturning = false;

            var targetMaxStackHeight = 0;
            var programCounter = 0;
            recordedStackHeight[0].Max = peakStackHeight;
            recordedStackHeight[0].Min = peakStackHeight;
            StackBounds currentStackBounds = recordedStackHeight[0];

            while (programCounter < code.Length)
            {
                var opcode = (Instruction)code[programCounter];
                (var inputs, var outputs, var immediates) = opcode.StackRequirements();

                var posPostInstruction = (ushort)(programCounter + 1);
                if (posPostInstruction > code.Length)
                {
                    if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, PC Reached out of bounds");
                    return false;
                }

                switch (opcode)
                {
                    case Instruction.CALLF or Instruction.JUMPF:
                        ushort targetSectionId = code.Slice(posPostInstruction, immediates.Value).ReadEthUInt16();
                        inputs = typesection[targetSectionId * MINIMUM_TYPESECTION_SIZE + INPUTS_OFFSET];

                        outputs = typesection[targetSectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
                        isTargetSectionNonReturning = typesection[targetSectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET] == 0x80;
                        outputs = (ushort)(isTargetSectionNonReturning ? 0 : outputs);
                        targetMaxStackHeight = typesection.Slice(targetSectionId * MINIMUM_TYPESECTION_SIZE + MAX_STACK_HEIGHT_OFFSET, EofValidator.TWO_BYTE_LENGTH).ReadEthUInt16();

                        if (MAX_STACK_HEIGHT - targetMaxStackHeight + inputs < currentStackBounds.Max)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, stack head during callf must not exceed {MAX_STACK_HEIGHT}");
                            return false;
                        }

                        if (opcode is Instruction.JUMPF && !isTargetSectionNonReturning && !(currrentSectionOutputs + inputs - outputs == currentStackBounds.Min && currentStackBounds.BoundsEqual()))
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Stack State invalid, required height {currrentSectionOutputs + inputs - outputs} but found {currentStackBounds.Max}");
                            return false;
                        }
                        break;
                    case Instruction.DUPN:
                        var imm_n = 1 + code[posPostInstruction];
                        inputs = (ushort)imm_n;
                        outputs = (ushort)(inputs + 1);
                        break;
                    case Instruction.SWAPN:
                        imm_n = 1 + code[posPostInstruction];
                        outputs = inputs = (ushort)(1 + imm_n);
                        break;
                    case Instruction.EXCHANGE:
                        imm_n = 1 + (byte)(code[posPostInstruction] >> 4);
                        var imm_m = 1 + (byte)(code[posPostInstruction] & 0x0F);
                        outputs = inputs = (ushort)(imm_n + imm_m + 1);
                        break;
                }

                if ((isTargetSectionNonReturning || opcode is not Instruction.JUMPF) && currentStackBounds.Min < inputs)
                {
                    if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Stack Underflow required {inputs} but found {currentStackBounds.Min}");
                    return false;
                }

                if (!opcode.IsTerminating())
                {
                    var delta = (short)(outputs - inputs);
                    currentStackBounds.Max += delta;
                    currentStackBounds.Min += delta;
                }
                peakStackHeight = Math.Max(peakStackHeight, currentStackBounds.Max);

                switch (opcode)
                {
                    case Instruction.RETF:
                        {
                            var expectedHeight = typesection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
                            if (expectedHeight != currentStackBounds.Min || !currentStackBounds.BoundsEqual())
                            {
                                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Stack state invalid required height {expectedHeight} but found {currentStackBounds.Min}");
                                return false;
                            }
                            break;
                        }
                    case Instruction.RJUMP or Instruction.RJUMPI:
                        {
                            short offset = code.Slice(programCounter + 1, immediates.Value).ReadEthInt16();
                            var jumpDestination = posPostInstruction + immediates.Value + offset;

                            if (opcode is Instruction.RJUMPI && (posPostInstruction + immediates.Value < recordedStackHeight.Length))
                                recordedStackHeight[posPostInstruction + immediates.Value].Combine(currentStackBounds);

                            if (jumpDestination > programCounter)
                                recordedStackHeight[jumpDestination].Combine(currentStackBounds);
                            else
                            {
                                if (recordedStackHeight[jumpDestination] != currentStackBounds)
                                {
                                    if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Stack state invalid at {jumpDestination}");
                                    return false;
                                }
                            }

                            break;
                        }
                    case Instruction.RJUMPV:
                        {
                            var count = code[posPostInstruction] + 1;
                            immediates = (ushort)(count * EofValidator.TWO_BYTE_LENGTH + EofValidator.ONE_BYTE_LENGTH);
                            for (short j = 0; j < count; j++)
                            {
                                int case_v = posPostInstruction + EofValidator.ONE_BYTE_LENGTH + j * EofValidator.TWO_BYTE_LENGTH;
                                int offset = code.Slice(case_v, EofValidator.TWO_BYTE_LENGTH).ReadEthInt16();
                                var jumpDestination = posPostInstruction + immediates.Value + offset;
                                if (jumpDestination > programCounter)
                                    recordedStackHeight[jumpDestination].Combine(currentStackBounds);
                                else
                                {
                                    if (recordedStackHeight[jumpDestination] != currentStackBounds)
                                    {
                                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Stack state invalid at {jumpDestination}");
                                        return false;
                                    }
                                }
                            }

                            posPostInstruction += immediates.Value;
                            if (posPostInstruction > code.Length)
                            {
                                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, PC Reached out of bounds");
                                return false;
                            }
                            break;
                        }
                }

                unreachedBytes -= 1 + immediates.Value;
                programCounter += 1 + immediates.Value;

                if (opcode.IsTerminating())
                {
                    if (programCounter < code.Length)
                    {
                        if (recordedStackHeight[programCounter].Max < 0)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, opcode not forward referenced, section {sectionId} pc {programCounter}");
                            return false;
                        }
                        currentStackBounds = recordedStackHeight[programCounter];
                    }
                }
                else
                {
                    if (programCounter < code.Length)
                    {
                        recordedStackHeight[programCounter].Combine(currentStackBounds);
                        currentStackBounds = recordedStackHeight[programCounter];
                    }
                }
            }

            if (unreachedBytes != 0)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, bytecode has unreachable segments");
                return false;
            }

            if (peakStackHeight != suggestedMaxHeight)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Suggested Max Stack height mismatches with actual Max, expected {suggestedMaxHeight} but found {peakStackHeight}");
                return false;
            }

            var result = peakStackHeight < MAX_STACK_HEIGHT;
            if (!result)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, stack overflow exceeded max stack height of {MAX_STACK_HEIGHT} but found {peakStackHeight}");
                return false;
            }
            return result;
        }
        finally
        {
            ArrayPool<StackBounds>.Shared.Return(recordedStackHeight);
        }
    }
}

