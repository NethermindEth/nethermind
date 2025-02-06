// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FastEnumUtility;
using Nethermind.Core.Extensions;
using Nethermind.Evm;

using static Nethermind.Evm.EvmObjectFormat.EofValidator;

namespace Nethermind.Evm.EvmObjectFormat.Handlers;

internal class Eof1 : IEofVersionHandler
{
    public const byte VERSION = 0x01;

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

    /// <summary>
    /// Attempts to parse the EOF header from the provided container memory.
    /// </summary>
    /// <param name="containerMemory">
    /// The memory containing the raw EOF data to parse.
    /// </param>
    /// <param name="validationStrategy">
    /// Flags that control additional validation (for example, whether to allow trailing bytes).
    /// </param>
    /// <param name="header">
    /// When this method returns, contains the parsed header if successful; otherwise, <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if the header was successfully parsed; otherwise, <c>false</c>.
    /// </returns>
    public bool TryParseEofHeader(ReadOnlyMemory<byte> containerMemory, ValidationStrategy validationStrategy, out EofHeader? header)
    {
        header = null;
        ReadOnlySpan<byte> container = containerMemory.Span;

        // Validate overall container size, magic value, and version.
        if (!ValidateBasicConstraints(container))
        {
            return false;
        }

        // The current read position; after the version byte.
        int pos = EofValidator.VERSION_OFFSET + 1;

        // Holds header size information for each section.
        Sizes sectionSizes = new();

        // These arrays hold the sizes for compound sections.
        int[]? codeSections = null;
        int[]? containerSections = null;

        bool continueParsing = true;
        while (continueParsing && pos < container.Length)
        {
            // Read the next separator that indicates which section comes next.
            Separator separator = (Separator)container[pos++];
            switch (separator)
            {
                case Separator.KIND_TYPE:
                    if (sectionSizes.TypeSectionSize != null)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, Multiple type sections");
                        return false;
                    }
                    if (!TryParseTypeSection(ref pos, out ushort typeSectionSize, container))
                    {
                        return false;
                    }
                    sectionSizes.TypeSectionSize = typeSectionSize;
                    break;

                case Separator.KIND_CODE:
                    if (sectionSizes.CodeSectionSize != null)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, Multiple code sections");
                        return false;
                    }
                    if (sectionSizes.TypeSectionSize is null)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, Code is not well formatted");
                        return false;
                    }
                    if (!TryParseCodeSection(ref pos, out codeSections, out ushort codeHeaderSize, container))
                    {
                        return false;
                    }
                    sectionSizes.CodeSectionSize = codeHeaderSize;
                    break;

                case Separator.KIND_CONTAINER:
                    if (sectionSizes.ContainerSectionSize != null)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, Multiple container sections");
                        return false;
                    }
                    if (sectionSizes.CodeSectionSize is null)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, Code is not well formatted");
                        return false;
                    }
                    if (sectionSizes.DataSectionSize != null)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, Container section is out of order");
                        return false;
                    }
                    if (!TryParseContainerSection(ref pos, out containerSections, out ushort containerHeaderSize, container))
                    {
                        return false;
                    }
                    sectionSizes.ContainerSectionSize = containerHeaderSize;
                    break;

                case Separator.KIND_DATA:
                    if (sectionSizes.DataSectionSize != null)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, Multiple data sections");
                        return false;
                    }
                    if (sectionSizes.CodeSectionSize is null)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, Code is not well formatted");
                        return false;
                    }
                    if (!TryParseDataSection(ref pos, out ushort dataSectionSize, container))
                    {
                        return false;
                    }
                    sectionSizes.DataSectionSize = dataSectionSize;
                    break;

                case Separator.TERMINATOR:
                    // The terminator must be followed by at least one byte.
                    if (container.Length < pos + EofValidator.ONE_BYTE_LENGTH)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
                        return false;
                    }
                    continueParsing = false;
                    break;

                default:
                    if (Logger.IsTrace)
                        Logger.Trace($"EOF: Eof{VERSION}, Code header is not well formatted");
                    return false;
            }
        }

        // Make sure mandatory sections (type, code, and data) were found.
        if (sectionSizes.TypeSectionSize is null || sectionSizes.CodeSectionSize is null || sectionSizes.DataSectionSize is null)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code is not well formatted");
            return false;
        }

        // Build the sub-headers for the various sections.
        var typeSectionHeader = new SectionHeader(pos, sectionSizes.TypeSectionSize.Value);
        var codeSectionHeader = new CompoundSectionHeader(typeSectionHeader.EndOffset, codeSections);
        CompoundSectionHeader? containerSectionHeader = containerSections is null ? null :
                                                          new CompoundSectionHeader(codeSectionHeader.EndOffset, containerSections);
        var dataSectionHeader = new SectionHeader(containerSectionHeader?.EndOffset ?? codeSectionHeader.EndOffset,
                                                   sectionSizes.DataSectionSize.Value);

        // Validate that the container does not have extra trailing bytes (unless allowed) and that
        // the data section is fully contained.
        if (!validationStrategy.HasFlag(ValidationStrategy.AllowTrailingBytes) &&
            dataSectionHeader.EndOffset < containerMemory.Length)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Extra data after end of container, starting at {dataSectionHeader.EndOffset}");
            return false;
        }
        if (validationStrategy.HasFlag(ValidationStrategy.Validate) &&
           !validationStrategy.HasFlag(ValidationStrategy.ValidateRuntimeMode) &&
            dataSectionHeader.EndOffset > container.Length)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Container has truncated data where full data is required");
            return false;
        }

        header = new EofHeader
        {
            Version = VERSION,
            PrefixSize = pos,
            TypeSection = typeSectionHeader,
            CodeSections = codeSectionHeader,
            ContainerSections = containerSectionHeader,
            DataSection = dataSectionHeader,
        };

        return true;
    }

    /// <summary>
    /// Validates overall constraints for the container (size, magic header, and version).
    /// </summary>
    /// <param name="container">The container data as a span.</param>
    /// <returns><c>true</c> if the basic constraints pass; otherwise, <c>false</c>.</returns>
    private static bool ValidateBasicConstraints(ReadOnlySpan<byte> container)
    {
        if (container.Length < MINIMUM_SIZE)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
            return false;
        }
        if (container.Length > MAXIMUM_SIZE)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code is larger than allowed maximum size of {MAXIMUM_SIZE}");
            return false;
        }
        if (!container.StartsWith(EofValidator.MAGIC))
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code doesn't start with magic byte sequence expected {EofValidator.MAGIC.ToHexString(true)} ");
            return false;
        }
        if (container[EofValidator.VERSION_OFFSET] != VERSION)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code is not Eof version {VERSION}");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Parses the type section header.
    /// </summary>
    /// <param name="pos">
    /// The current read position. On success, this is advanced past the type section header.
    /// </param>
    /// <param name="typeSectionSize">The size of the type section as read from the header.</param>
    /// <param name="container">The container span.</param>
    /// <returns><c>true</c> if the type section header was parsed successfully; otherwise, <c>false</c>.</returns>
    private static bool TryParseTypeSection(ref int pos, out ushort typeSectionSize, ReadOnlySpan<byte> container)
    {
        typeSectionSize = 0;
        // Ensure enough bytes are available to read the type section size.
        if (container.Length < pos + EofValidator.TWO_BYTE_LENGTH)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
            return false;
        }
        typeSectionSize = GetUInt16(pos, container);
        if (typeSectionSize < MINIMUM_TYPESECTION_SIZE)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, TypeSection Size must be at least {MINIMUM_TYPESECTION_SIZE}, but found {typeSectionSize}");
            return false;
        }
        pos += EofValidator.TWO_BYTE_LENGTH;
        return true;
    }

    /// <summary>
    /// Parses the code section header and its list of section sizes.
    /// </summary>
    /// <param name="pos">
    /// The current read position. On success, this is advanced past the code section header.
    /// </param>
    /// <param name="codeSections">
    /// On success, the array of individual code section sizes.
    /// </param>
    /// <param name="headerSize">
    /// On success, the total header size (in bytes) for the code section.
    /// </param>
    /// <param name="container">The container span.</param>
    /// <returns><c>true</c> if the code section header was parsed successfully; otherwise, <c>false</c>.</returns>
    private static bool TryParseCodeSection(ref int pos, out int[]? codeSections, out ushort headerSize, ReadOnlySpan<byte> container)
    {
        codeSections = null;
        headerSize = 0;
        // Must have enough bytes to read the count of code sections.
        if (container.Length < pos + EofValidator.TWO_BYTE_LENGTH)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
            return false;
        }

        ushort numberOfCodeSections = GetUInt16(pos, container);
        headerSize = (ushort)(numberOfCodeSections * EofValidator.TWO_BYTE_LENGTH);

        if (numberOfCodeSections > MAXIMUM_NUM_CODE_SECTIONS)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, code sections count must not exceed {MAXIMUM_NUM_CODE_SECTIONS}");
            return false;
        }

        int requiredLength = pos + EofValidator.TWO_BYTE_LENGTH + (numberOfCodeSections * EofValidator.TWO_BYTE_LENGTH);
        if (container.Length < requiredLength)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
            return false;
        }

        codeSections = new int[numberOfCodeSections];
        int headerStart = pos + EofValidator.TWO_BYTE_LENGTH;
        for (int i = 0; i < codeSections.Length; i++)
        {
            int currentOffset = headerStart + (i * EofValidator.TWO_BYTE_LENGTH);
            int codeSectionSize = GetUInt16(currentOffset, container);
            if (codeSectionSize == 0)
            {
                if (Logger.IsTrace)
                    Logger.Trace($"EOF: Eof{VERSION}, Empty Code Section are not allowed, CodeSectionSize must be > 0 but found {codeSectionSize}");
                return false;
            }
            codeSections[i] = codeSectionSize;
        }
        pos += EofValidator.TWO_BYTE_LENGTH + (numberOfCodeSections * EofValidator.TWO_BYTE_LENGTH);
        return true;
    }

    /// <summary>
    /// Parses the container section header and its list of section sizes.
    /// </summary>
    /// <param name="pos">
    /// The current read position. On success, this is advanced past the container section header.
    /// </param>
    /// <param name="containerSections">
    /// On success, the array of individual container section sizes.
    /// </param>
    /// <param name="headerSize">
    /// On success, the total header size (in bytes) for the container section.
    /// </param>
    /// <param name="container">The container span.</param>
    /// <returns><c>true</c> if the container section header was parsed successfully; otherwise, <c>false</c>.</returns>
    private static bool TryParseContainerSection(ref int pos, out int[]? containerSections, out ushort headerSize, ReadOnlySpan<byte> container)
    {
        containerSections = null;
        headerSize = 0;
        if (container.Length < pos + EofValidator.TWO_BYTE_LENGTH)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
            return false;
        }

        ushort numberOfContainerSections = GetUInt16(pos, container);
        headerSize = (ushort)(numberOfContainerSections * EofValidator.TWO_BYTE_LENGTH);

        // Enforce that the count is not zero and does not exceed the maximum allowed.
        if (numberOfContainerSections == 0 || numberOfContainerSections > (MAXIMUM_NUM_CONTAINER_SECTIONS + 1))
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, container sections count must not exceed {MAXIMUM_NUM_CONTAINER_SECTIONS}");
            return false;
        }

        int requiredLength = pos + EofValidator.TWO_BYTE_LENGTH + (numberOfContainerSections * EofValidator.TWO_BYTE_LENGTH);
        if (container.Length < requiredLength)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
            return false;
        }

        containerSections = new int[numberOfContainerSections];
        int headerStart = pos + EofValidator.TWO_BYTE_LENGTH;
        for (int i = 0; i < containerSections.Length; i++)
        {
            int currentOffset = headerStart + (i * EofValidator.TWO_BYTE_LENGTH);
            int containerSectionSize = GetUInt16(currentOffset, container);
            if (containerSectionSize == 0)
            {
                if (Logger.IsTrace)
                    Logger.Trace($"EOF: Eof{VERSION}, Empty Container Section are not allowed, containerSectionSize must be > 0 but found {containerSectionSize}");
                return false;
            }
            containerSections[i] = containerSectionSize;
        }
        pos += EofValidator.TWO_BYTE_LENGTH + (numberOfContainerSections * EofValidator.TWO_BYTE_LENGTH);
        return true;
    }

    /// <summary>
    /// Parses the data section header.
    /// </summary>
    /// <param name="pos">
    /// The current read position. On success, this is advanced past the data section header.
    /// </param>
    /// <param name="dataSectionSize">
    /// On success, the size of the data section as specified in the header.
    /// </param>
    /// <param name="container">The container span.</param>
    /// <returns><c>true</c> if the data section header was parsed successfully; otherwise, <c>false</c>.</returns>
    private static bool TryParseDataSection(ref int pos, out ushort dataSectionSize, ReadOnlySpan<byte> container)
    {
        dataSectionSize = 0;
        if (container.Length < pos + EofValidator.TWO_BYTE_LENGTH)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
            return false;
        }
        dataSectionSize = GetUInt16(pos, container);
        pos += EofValidator.TWO_BYTE_LENGTH;
        return true;
    }

    /// <summary>
    /// Reads an unsigned 16-bit integer from the specified container at the given offset.
    /// </summary>
    /// <param name="offset">The offset within the span to read from.</param>
    /// <param name="container">The byte span containing the data.</param>
    /// <returns>The 16‐bit unsigned integer value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort GetUInt16(int offset, ReadOnlySpan<byte> container) =>
        container.Slice(offset, EofValidator.TWO_BYTE_LENGTH).ReadEthUInt16();

    /// <summary>
    /// Attempts to create an <see cref="EofContainer"/> from the provided raw code data.
    /// </summary>
    /// <param name="validationStrategy">
    /// The flags indicating which validation steps to perform (e.g. whether full container validation is required).
    /// </param>
    /// <param name="eofContainer">
    /// When this method returns, contains the parsed <see cref="EofContainer"/> if successful; otherwise, <c>null</c>.
    /// </param>
    /// <param name="code">
    /// The raw code data as a <see cref="ReadOnlyMemory{T}"/>.
    /// </param>
    /// <returns>
    /// <c>true</c> if a valid EOF container was created from the code; otherwise, <c>false</c>.
    /// </returns>
    public bool TryGetEofContainer(ValidationStrategy validationStrategy, [NotNullWhen(true)] out EofContainer? eofContainer, ReadOnlyMemory<byte> code)
    {
        eofContainer = null;

        // Step 1: Attempt to parse the header from the code.
        if (!TryParseEofHeader(code, validationStrategy, out EofHeader? header))
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Header not parsed");
            return false;
        }

        // Step 2: Validate the body using the parsed header and the full code span.
        if (!ValidateBody(header.Value, validationStrategy, code.Span))
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Body not valid");
            return false;
        }

        // Step 3: Construct the container using the raw code and the parsed header.
        eofContainer = new EofContainer(code, header.Value);

        // Step 4: If full validation is requested, verify the container's integrity.
        if (validationStrategy.HasFlag(ValidationStrategy.Validate))
        {
            if (!ValidateContainer(eofContainer.Value, validationStrategy))
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Container not valid");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates the integrity of the given EOF container, including its nested container sections,
    /// according to the provided <see cref="ValidationStrategy"/>.
    /// </summary>
    /// <param name="eofContainer">The EOF container to validate.</param>
    /// <param name="validationStrategy">The strategy flags controlling the level of validation.</param>
    /// <returns><c>true</c> if the container and all its nested sections are valid; otherwise, <c>false</c>.</returns>
    private bool ValidateContainer(in EofContainer eofContainer, ValidationStrategy validationStrategy)
    {
        // Use a FIFO queue to process nested containers.
        // Each entry pairs a container with its associated validation strategy.
        Queue<(EofContainer container, ValidationStrategy strategy)> containers = new();
        containers.Enqueue((eofContainer, validationStrategy));

        // Process each container (including nested ones) until none remain.
        while (containers.TryDequeue(out (EofContainer container, ValidationStrategy strategy) target))
        {
            // Process the current container. If validation fails at any level, return false.
            if (!ProcessContainer(target.container, target.strategy, containers))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Processes a single container by validating its code sections and any nested container sections.
    /// </summary>
    /// <param name="targetContainer">The container to process.</param>
    /// <param name="strategy">The validation strategy to apply.</param>
    /// <param name="containers">
    /// A queue into which any nested container (subsection) that requires further validation will be enqueued.
    /// </param>
    /// <returns><c>true</c> if all sections in the container are valid; otherwise, <c>false</c>.</returns>
    private bool ProcessContainer(in EofContainer targetContainer, ValidationStrategy strategy,
                                  Queue<(EofContainer container, ValidationStrategy strategy)> containers)
    {
        // Create a work queue to traverse the container’s sections.
        // The capacity is 1 (for the main container’s code sections) plus any nested container sections.
        QueueManager containerQueue = new(1 + (targetContainer.Header.ContainerSections?.Count ?? 0));

        // Enqueue index 0 to represent the main container's code section.
        containerQueue.Enqueue(0, strategy);
        containerQueue.VisitedContainers[0] = GetValidation(strategy);

        // Process each work item in the container queue.
        while (containerQueue.TryDequeue(out (int Index, ValidationStrategy Strategy) worklet))
        {
            // Worklet index 0 represents the primary container's code sections.
            if (worklet.Index == 0)
            {
                if (!ValidateCodeSections(targetContainer, worklet.Strategy, in containerQueue))
                {
                    if (Logger.IsTrace)
                        Logger.Trace($"EOF: Eof{VERSION}, Code sections invalid");
                    return false;
                }
            }
            else
            {
                // Process a nested container section.
                if (!ProcessContainerSection(targetContainer, worklet.Index, worklet.Strategy, containers))
                {
                    return false;
                }
            }

            // Mark the current worklet as visited using a helper value derived from the strategy.
            containerQueue.MarkVisited(worklet.Index, GetVisited(worklet.Strategy));
        }

        // After processing, all expected work items must have been visited.
        if (!containerQueue.IsAllVisited())
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Not all containers visited");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Processes a container section (i.e. a nested container) by parsing its header and validating its body.
    /// If the current strategy indicates full validation, the new container is enqueued for further processing.
    /// </summary>
    /// <param name="targetContainer">The parent container that holds the container sections.</param>
    /// <param name="workletIndex">
    /// The worklet index representing the container section. (A value of 1 corresponds to the first container section.)
    /// </param>
    /// <param name="strategy">The validation strategy to apply.</param>
    /// <param name="containers">
    /// The queue to which a newly constructed nested container will be enqueued if further validation is required.
    /// </param>
    /// <returns><c>true</c> if the container section is valid or skipped; otherwise, <c>false</c>.</returns>
    private bool ProcessContainerSection(in EofContainer targetContainer, int workletIndex,
                                         ValidationStrategy strategy,
                                         Queue<(EofContainer container, ValidationStrategy strategy)> containers)
    {
        // If the worklet index exceeds the number of available container sections, skip processing.
        if (targetContainer.ContainerSections.Length < workletIndex)
            return true;

        // Adjust the section index (worklet indices start at 1 for container sections).
        int section = workletIndex - 1;
        ReadOnlyMemory<byte> subsection = targetContainer.ContainerSections[section];

        // Parse the header for the nested container section.
        if (!TryParseEofHeader(subsection, strategy, out EofHeader? header))
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Header invalid: section {section}");
            return false;
        }

        // Validate the body of the nested container section.
        if (!ValidateBody(header.Value, strategy, subsection.Span))
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Body invalid: section {section}");
            return false;
        }

        // If full validation is required, enqueue the new nested container for further processing.
        if (strategy.HasFlag(ValidationStrategy.Validate))
        {
            containers.Enqueue((new EofContainer(subsection, header.Value), strategy));
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

    private static bool ValidateBody(in EofHeader header, ValidationStrategy strategy, ReadOnlySpan<byte> container)
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
        SectionHeader typeSection = header.TypeSection;
        (int typeSectionStart, int typeSectionSize) = (typeSection.Start, typeSection.Size);

        if (header.ContainerSections?.Count > MAXIMUM_NUM_CONTAINER_SECTIONS + 1)
        {
            // move this check where `header.ExtraContainers.Count` is parsed
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, InitCode Containers count must be less than {MAXIMUM_NUM_CONTAINER_SECTIONS} but found {header.ContainerSections?.Count}");
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

        ReadOnlySpan<byte> typeSectionBytes = container.Slice(typeSectionStart, typeSectionSize);
        if (!ValidateTypeSection(typeSectionBytes))
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, invalid TypeSection found");
            return false;
        }

        return true;
    }

    private static bool ValidateCodeSections(in EofContainer eofContainer, ValidationStrategy strategy, in QueueManager containerQueue)
    {
        QueueManager sectionQueue = new(eofContainer.Header.CodeSections.Count);

        sectionQueue.Enqueue(0, strategy);

        while (sectionQueue.TryDequeue(out (int Index, ValidationStrategy Strategy) sectionIdx))
        {
            if (sectionQueue.VisitedContainers[sectionIdx.Index] != 0)
                continue;

            if (!ValidateInstructions(eofContainer, sectionIdx.Index, strategy, in sectionQueue, in containerQueue))
                return false;

            sectionQueue.MarkVisited(sectionIdx.Index, ValidationStrategy.Validate);
        }

        return sectionQueue.IsAllVisited();
    }

    private static bool ValidateTypeSection(ReadOnlySpan<byte> types)
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

    private static bool ValidateInstructions(in EofContainer eofContainer, int sectionId, ValidationStrategy strategy, in QueueManager sectionsWorklist, in QueueManager containersWorklist)
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
            bool hasRequiredSectionExit = isCurrentSectionNonReturning;

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

                    containersWorklist.Enqueue(runtimeContainerId + 1, ValidationStrategy.ValidateRuntimeMode | ValidationStrategy.ValidateFullBody);

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
                    hasRequiredSectionExit = true;
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

                    if (isCurrentSectionNonReturning && !isTargetSectionNonReturning)
                    {
                        if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, {Instruction.JUMPF} from non-returning must target non-returning");
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
                else if (opcode is Instruction.RETF)
                {
                    hasRequiredSectionExit = true;
                    if (isCurrentSectionNonReturning)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, non returning sections are not allowed to use opcode {Instruction.RETF}");
                        return false;
                    }
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

            if (!hasRequiredSectionExit)
            {
                if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Code section {sectionId} is returning and does not have a RETF or JUMPF");
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

    public static bool ValidateStackState(int sectionId, ReadOnlySpan<byte> code, ReadOnlySpan<byte> typeSection)
    {
        StackBounds[] recordedStackHeight = ArrayPool<StackBounds>.Shared.Rent(code.Length);
        Array.Fill(recordedStackHeight, new StackBounds());

        try
        {
            ushort suggestedMaxHeight = typeSection.Slice(sectionId * MINIMUM_TYPESECTION_SIZE + EofValidator.TWO_BYTE_LENGTH, EofValidator.TWO_BYTE_LENGTH).ReadEthUInt16();

            ushort currentSectionOutputs = typeSection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET] == 0x80 ? (ushort)0 : typeSection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
            short peakStackHeight = typeSection[sectionId * MINIMUM_TYPESECTION_SIZE + INPUTS_OFFSET];

            var unreachedBytes = code.Length;
            var isTargetSectionNonReturning = false;
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
                        inputs = typeSection[targetSectionId * MINIMUM_TYPESECTION_SIZE + INPUTS_OFFSET];

                        outputs = typeSection[targetSectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
                        isTargetSectionNonReturning = typeSection[targetSectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET] == 0x80;
                        outputs = (ushort)(isTargetSectionNonReturning ? 0 : outputs);
                        int targetMaxStackHeight = typeSection.Slice(targetSectionId * MINIMUM_TYPESECTION_SIZE + MAX_STACK_HEIGHT_OFFSET, EofValidator.TWO_BYTE_LENGTH).ReadEthUInt16();

                        if (MAX_STACK_HEIGHT - targetMaxStackHeight + inputs < currentStackBounds.Max)
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, stack head during callf must not exceed {MAX_STACK_HEIGHT}");
                            return false;
                        }

                        if (opcode is Instruction.JUMPF && !isTargetSectionNonReturning && !(currentSectionOutputs + inputs - outputs == currentStackBounds.Min && currentStackBounds.BoundsEqual()))
                        {
                            if (Logger.IsTrace) Logger.Trace($"EOF: Eof{VERSION}, Stack State invalid, required height {currentSectionOutputs + inputs - outputs} but found {currentStackBounds.Max}");
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
                            var expectedHeight = typeSection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
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

    private readonly struct QueueManager(int containerCount)
    {
        public readonly Queue<(int index, ValidationStrategy strategy)> ContainerQueue = new();
        public readonly ValidationStrategy[] VisitedContainers = new ValidationStrategy[containerCount];

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
            Max = Math.Max(Max, other.Max);
            Min = Math.Min(Min, other.Min);
        }

        public readonly bool BoundsEqual() => Max == Min;

        public static bool operator ==(StackBounds left, StackBounds right) => left.Max == right.Max && right.Min == left.Min;
        public static bool operator !=(StackBounds left, StackBounds right) => !(left == right);
        public override readonly bool Equals(object obj) => obj is StackBounds bounds && this == bounds;
        public override readonly int GetHashCode() => (Max << 16) | (int)Min;
    }

    private ref struct Sizes
    {
        public ushort? TypeSectionSize;
        public ushort? CodeSectionSize;
        public ushort? DataSectionSize;
        public ushort? ContainerSectionSize;
    }

    internal enum Separator : byte
    {
        KIND_TYPE = 0x01,
        KIND_CODE = 0x02,
        KIND_CONTAINER = 0x03,
        KIND_DATA = 0x04,
        TERMINATOR = 0x00
    }
}

