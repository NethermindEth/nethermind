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

// https://github.com/ipsilon/eof/blob/main/spec/eof.md
internal class Eof1 : IEofVersionHandler
{
    public const byte VERSION = 0x01;

    internal const byte MINIMUM_HEADER_SECTION_SIZE = 3;
    internal const byte MINIMUM_TYPESECTION_SIZE = 4;
    internal const byte MINIMUM_CODESECTION_SIZE = 1;
    internal const byte MINIMUM_DATASECTION_SIZE = 0;
    internal const byte MINIMUM_CONTAINERSECTION_SIZE = 0;
    internal const byte MINIMUM_HEADER_SIZE = VERSION_OFFSET
                                            + MINIMUM_HEADER_SECTION_SIZE
                                            + MINIMUM_HEADER_SECTION_SIZE + TWO_BYTE_LENGTH
                                            + MINIMUM_HEADER_SECTION_SIZE
                                            + ONE_BYTE_LENGTH;

    internal const byte BYTE_BIT_COUNT = 8; // indicates the length of the count immediate of JumpV
    internal const byte MINIMUMS_ACCEPTABLE_JUMPV_JUMPTABLE_LENGTH = 1; // indicates the length of the count immediate of JumpV

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
    internal const int MAXIMUM_NUM_CONTAINER_SECTIONS = 0x00FF;
    internal const ushort RETURN_STACK_MAX_HEIGHT = MAXIMUM_NUM_CODE_SECTIONS; // the size in the type section allocated to each function section

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
        int pos = VERSION_OFFSET + 1;

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
                    if (container.Length < pos + ONE_BYTE_LENGTH)
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
        if (!container.StartsWith(MAGIC))
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code doesn't start with magic byte sequence expected {MAGIC.ToHexString(true)} ");
            return false;
        }
        if (container[VERSION_OFFSET] != VERSION)
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
        if (container.Length < pos + TWO_BYTE_LENGTH)
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
        pos += TWO_BYTE_LENGTH;
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
        if (container.Length < pos + TWO_BYTE_LENGTH)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
            return false;
        }

        ushort numberOfCodeSections = GetUInt16(pos, container);
        headerSize = (ushort)(numberOfCodeSections * TWO_BYTE_LENGTH);

        if (numberOfCodeSections > MAXIMUM_NUM_CODE_SECTIONS)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, code sections count must not exceed {MAXIMUM_NUM_CODE_SECTIONS}");
            return false;
        }

        int requiredLength = pos + TWO_BYTE_LENGTH + (numberOfCodeSections * TWO_BYTE_LENGTH);
        if (container.Length < requiredLength)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
            return false;
        }

        codeSections = new int[numberOfCodeSections];
        int headerStart = pos + TWO_BYTE_LENGTH;
        for (int i = 0; i < codeSections.Length; i++)
        {
            int currentOffset = headerStart + (i * TWO_BYTE_LENGTH);
            int codeSectionSize = GetUInt16(currentOffset, container);
            if (codeSectionSize == 0)
            {
                if (Logger.IsTrace)
                    Logger.Trace($"EOF: Eof{VERSION}, Empty Code Section are not allowed, CodeSectionSize must be > 0 but found {codeSectionSize}");
                return false;
            }
            codeSections[i] = codeSectionSize;
        }
        pos += TWO_BYTE_LENGTH + (numberOfCodeSections * TWO_BYTE_LENGTH);
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
        if (container.Length < pos + TWO_BYTE_LENGTH)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
            return false;
        }

        ushort numberOfContainerSections = GetUInt16(pos, container);
        headerSize = (ushort)(numberOfContainerSections * TWO_BYTE_LENGTH);

        // Enforce that the count is not zero and does not exceed the maximum allowed.
        if (numberOfContainerSections == 0 || numberOfContainerSections > (MAXIMUM_NUM_CONTAINER_SECTIONS + 1))
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, container sections count must not exceed {MAXIMUM_NUM_CONTAINER_SECTIONS}");
            return false;
        }

        int requiredLength = pos + TWO_BYTE_LENGTH + (numberOfContainerSections * TWO_BYTE_LENGTH);
        if (container.Length < requiredLength)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
            return false;
        }

        containerSections = new int[numberOfContainerSections];
        int headerStart = pos + TWO_BYTE_LENGTH;
        for (int i = 0; i < containerSections.Length; i++)
        {
            int currentOffset = headerStart + (i * TWO_BYTE_LENGTH);
            int containerSectionSize = GetUInt16(currentOffset, container);
            if (containerSectionSize == 0)
            {
                if (Logger.IsTrace)
                    Logger.Trace($"EOF: Eof{VERSION}, Empty Container Section are not allowed, containerSectionSize must be > 0 but found {containerSectionSize}");
                return false;
            }
            containerSections[i] = containerSectionSize;
        }
        pos += TWO_BYTE_LENGTH + (numberOfContainerSections * TWO_BYTE_LENGTH);
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
        if (container.Length < pos + TWO_BYTE_LENGTH)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Code is too small to be valid code");
            return false;
        }
        dataSectionSize = GetUInt16(pos, container);
        pos += TWO_BYTE_LENGTH;
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
        container.Slice(offset, TWO_BYTE_LENGTH).ReadEthUInt16();

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
        if (!containerQueue.IsAllVisitedAndNotAmbiguous())
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

    private static ValidationStrategy GetVisited(ValidationStrategy validationStrategy) => validationStrategy.HasFlag(ValidationStrategy.ValidateInitCodeMode)
            ? ValidationStrategy.ValidateInitCodeMode
            : ValidationStrategy.ValidateRuntimeMode;

    private static ValidationStrategy GetValidation(ValidationStrategy validationStrategy) => validationStrategy.HasFlag(ValidationStrategy.ValidateInitCodeMode)
            ? ValidationStrategy.ValidateInitCodeMode
            : validationStrategy.HasFlag(ValidationStrategy.ValidateRuntimeMode)
                ? ValidationStrategy.ValidateRuntimeMode
                : ValidationStrategy.None;

    /// <summary>
    /// Validates the body of the EOF container, ensuring that the various sections (code, data, and type)
    /// are consistent with the metadata specified in the header.
    /// </summary>
    /// <param name="header">
    /// The parsed EOF header containing metadata about section boundaries and sizes.
    /// </param>
    /// <param name="strategy">
    /// The flags controlling which validation rules are applied.
    /// </param>
    /// <param name="container">
    /// The complete container data as a read-only span of bytes.
    /// </param>
    /// <returns>
    /// <c>true</c> if the body is valid according to the header and strategy; otherwise, <c>false</c>.
    /// </returns>
    private static bool ValidateBody(in EofHeader header, ValidationStrategy strategy, ReadOnlySpan<byte> container)
    {
        // 1. Validate overall offsets and the contract (code) body length.
        int startOffset = header.TypeSection.Start;
        int endOffset = header.DataSection.Start;

        // Ensure the DataSection starts within the container.
        if (endOffset > container.Length)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, DataSectionStart ({endOffset}) exceeds container length ({container.Length}).");
            return false;
        }

        // Calculate the expected length of the "contract body" (combined code sections)
        int calculatedCodeLength = header.TypeSection.Size
                                   + header.CodeSections.Size
                                   + (header.ContainerSections?.Size ?? 0);

        // Extract the contract body and the data body segments.
        ReadOnlySpan<byte> contractBody = container[startOffset..endOffset];
        ReadOnlySpan<byte> dataBody = container[endOffset..];

        // The contract body length must exactly match the sum of the sizes indicated in the header.
        if (contractBody.Length != calculatedCodeLength)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Contract body length ({contractBody.Length}) does not match calculated code length ({calculatedCodeLength}).");
            return false;
        }

        // 2. Validate the container sections count.
        // Is one extra from the initial so one extra to max count
        if (header.ContainerSections?.Count > MAXIMUM_NUM_CONTAINER_SECTIONS + 1)
        {
            // NOTE: This check could be moved to the header parsing phase.
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Container sections count ({header.ContainerSections?.Count}) exceeds allowed maximum ({MAXIMUM_NUM_CONTAINER_SECTIONS}).");
            return false;
        }

        // 3. Validate the DataSection against the provided strategy.
        if (!ValidateDataSection(header, strategy, dataBody))
        {
            return false;
        }

        // 4. Validate that the CodeSections are non-empty and their sizes are valid.
        CompoundSectionHeader codeSections = header.CodeSections;
        if (!ValidateCodeSectionsNonZero(codeSections))
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, CodeSection count ({codeSections.Count}) is zero or contains an empty section.");
            return false;
        }

        // The number of code sections should match the number of type entries,
        // which is derived by dividing the TypeSection size by the minimum type section size.
        int expectedTypeCount = header.TypeSection.Size / MINIMUM_TYPESECTION_SIZE;
        if (codeSections.Count != expectedTypeCount)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, CodeSections count ({codeSections.Count}) does not match expected type count ({expectedTypeCount}).");
            return false;
        }

        // 5. Validate the content of the TypeSection.
        ReadOnlySpan<byte> typeSectionBytes = container.Slice(header.TypeSection.Start, header.TypeSection.Size);
        if (!ValidateTypeSection(typeSectionBytes))
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Invalid TypeSection content.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates the DataSection of the container based on the validation strategy.
    /// </summary>
    /// <param name="header">
    /// The header containing the DataSection metadata.
    /// </param>
    /// <param name="strategy">
    /// The validation strategy flags that determine the validation rules.
    /// </param>
    /// <param name="dataBody">
    /// The slice of the container representing the data section.
    /// </param>
    /// <returns>
    /// <c>true</c> if the DataSection is valid; otherwise, <c>false</c>.
    /// </returns>
    private static bool ValidateDataSection(in EofHeader header, ValidationStrategy strategy, ReadOnlySpan<byte> dataBody)
    {
        // If full body validation is requested, or it is runtime mode (but not both)
        // the DataSection size must be less than or equal to the data available.
        if ((strategy.HasFlag(ValidationStrategy.ValidateRuntimeMode) ^ strategy.HasFlag(ValidationStrategy.ValidateFullBody))
            && header.DataSection.Size > dataBody.Length)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, DataSection size ({header.DataSection.Size}) exceeds available data ({dataBody.Length}).");
            return false;
        }

        // When trailing bytes are not allowed, the DataSection cannot exceed the stated size data.
        // Undeflow cases were checked above as they don't apply in all cases
        if (!strategy.HasFlag(ValidationStrategy.AllowTrailingBytes)
            && strategy.HasFlag(ValidationStrategy.ValidateFullBody)
            && header.DataSection.Size < dataBody.Length)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, DataSection size ({header.DataSection.Size}) does not match available data ({dataBody.Length}).");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates that the CodeSections are non-empty and that none of the subsection sizes are zero.
    /// </summary>
    /// <param name="codeSections">
    /// The compound header for the code sections.
    /// </param>
    /// <returns>
    /// <c>true</c> if the code sections are valid; otherwise, <c>false</c>.
    /// </returns>
    private static bool ValidateCodeSectionsNonZero(CompoundSectionHeader codeSections) =>
        // The code sections must contain at least one subsection and none may have a size of zero.
        codeSections.Count > 0 && !codeSections.SubSectionsSizes.Any(size => size == 0);

    /// <summary>
    /// Validates the instructions in all code sections of the provided EOF container.
    /// </summary>
    /// <param name="eofContainer">
    /// The EOF container that holds the code sections to be validated.
    /// </param>
    /// <param name="strategy">
    /// The validation strategy to use when validating the instructions.
    /// </param>
    /// <param name="containerQueue">
    /// A <see cref="QueueManager"/> instance tracking nested container processing,
    /// which is used during instruction validation.
    /// </param>
    /// <returns>
    /// <c>true</c> if all code sections have been validated successfully; otherwise, <c>false</c>.
    /// </returns>
    private static bool ValidateCodeSections(in EofContainer eofContainer, ValidationStrategy strategy, in QueueManager containerQueue)
    {
        // Initialize a queue manager for the code sections. The queue capacity is set
        // to the number of code sections in the container header.
        QueueManager sectionQueue = new(eofContainer.Header.CodeSections.Count);

        // Enqueue the primary code section (index 0) with the given strategy.
        sectionQueue.Enqueue(0, strategy);

        // Process each code section until the sectionQueue is empty.
        while (sectionQueue.TryDequeue(out (int Index, ValidationStrategy Strategy) sectionIdx))
        {
            // If this section has already been processed, skip it.
            if (sectionQueue.VisitedContainers[sectionIdx.Index] != 0)
            {
                continue;
            }

            // Validate the instructions in the current code section.
            // This method call is responsible for checking the validity of the instructions
            // within the code section at the given index.
            if (!ValidateInstructions(eofContainer, sectionIdx.Index, strategy, in sectionQueue, in containerQueue))
            {
                return false;
            }

            // Mark the current code section as visited to avoid duplicate processing.
            sectionQueue.MarkVisited(sectionIdx.Index, ValidationStrategy.Validate);
        }

        // After processing, confirm that all expected code sections were visited.
        return sectionQueue.IsAllVisitedAndNotAmbiguous();
    }

    /// <summary>
    /// Validates the type section of an EOF container by verifying that the section header
    /// and each individual type entry conform to the expected format and limits.
    /// </summary>
    /// <param name="types">
    /// A read-only span of bytes representing the type section.
    /// </param>
    /// <returns>
    /// <c>true</c> if the type section is valid; otherwise, <c>false</c>.
    /// </returns>
    private static bool ValidateTypeSection(ReadOnlySpan<byte> types)
    {
        // The first type entry must have 0 inputs and a specific non-returning output indicator.
        if (types[INPUTS_OFFSET] != 0 || types[OUTPUTS_OFFSET] != NON_RETURNING)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, first 2 bytes of type section must be 0s");
            return false;
        }

        // The total length of the type section must be an integer multiple of the fixed entry size.
        if (types.Length % MINIMUM_TYPESECTION_SIZE != 0)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, type section length must be a product of {MINIMUM_TYPESECTION_SIZE}");
            return false;
        }

        // Process each type section entry.
        for (var offset = 0; offset < types.Length; offset += MINIMUM_TYPESECTION_SIZE)
        {
            // Extract the current entry.
            ReadOnlySpan<byte> entry = types.Slice(offset, MINIMUM_TYPESECTION_SIZE);

            // Validate the individual type entry.
            if (!ValidateTypeSectionEntry(entry))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Validates an individual type section entry by checking the input count, output count,
    /// and maximum stack height against defined limits.
    /// </summary>
    /// <param name="entry">
    /// A read-only span of bytes representing a single type section entry.
    /// </param>
    /// <returns>
    /// <c>true</c> if the entry is valid; otherwise, <c>false</c>.
    /// </returns>
    private static bool ValidateTypeSectionEntry(ReadOnlySpan<byte> entry)
    {
        // Retrieve the input and output counts from the fixed offsets.
        byte inputCount = entry[INPUTS_OFFSET];
        byte outputCount = entry[OUTPUTS_OFFSET];

        // Read the maximum stack height (a 16-bit value) from the designated slice.
        ushort maxStackHeight = entry.Slice(MAX_STACK_HEIGHT_OFFSET, MAX_STACK_HEIGHT_LENGTH).ReadEthUInt16();

        // Validate that the input count does not exceed the allowed maximum.
        if (inputCount > INPUTS_MAX)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Too many inputs: {inputCount}");
            return false;
        }

        // Validate that the output count is within allowed limits.
        // The exception is if the output count is set to NON_RETURNING.
        if (outputCount > OUTPUTS_MAX && outputCount != NON_RETURNING)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Too many outputs: {outputCount}");
            return false;
        }

        // Ensure the maximum stack height does not exceed the defined limit.
        if (maxStackHeight > MAX_STACK_HEIGHT)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Stack depth too high: {maxStackHeight}");
            return false;
        }

        return true;
    }
    /// <summary>
    /// Validates the instructions of the given code section in an EOF container.
    /// </summary>
    /// <param name="eofContainer">The container holding the EOF bytecode and type sections.</param>
    /// <param name="sectionId">The index of the code section to validate.</param>
    /// <param name="strategy">The validation strategy (e.g. runtime or InitCode mode).</param>
    /// <param name="sectionsWorklist">A queue manager for additional code sections to be validated.</param>
    /// <param name="containersWorklist">A queue manager for container sections that need validation.</param>
    /// <returns>True if the code section is valid; otherwise, false.</returns>
    private static bool ValidateInstructions(
        in EofContainer eofContainer,
        int sectionId,
        ValidationStrategy strategy,
        in QueueManager sectionsWorklist,
        in QueueManager containersWorklist)
    {
        ReadOnlySpan<byte> code = eofContainer.CodeSections[sectionId].Span;

        // A code section must contain at least one byte.
        if (code.Length < 1)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, CodeSection {sectionId} is too short to be valid");
            return false;
        }

        // Allocate temporary bitmaps for tracking jump destinations.
        int bitmapLength = code.Length / BYTE_BIT_COUNT + 1;
        byte[] invalidJmpLocationArray = ArrayPool<byte>.Shared.Rent(bitmapLength);
        byte[] jumpDestinationsArray = ArrayPool<byte>.Shared.Rent(bitmapLength);

        try
        {
            // Ensure that we only work on the portion of the rented arrays that we need.
            Span<byte> invalidJumpDestinations = invalidJmpLocationArray.AsSpan(0, bitmapLength);
            Span<byte> jumpDestinations = jumpDestinationsArray.AsSpan(0, bitmapLength);
            invalidJumpDestinations.Clear();
            jumpDestinations.Clear();

            ReadOnlySpan<byte> currentTypeSection = eofContainer.TypeSections[sectionId].Span;
            bool isCurrentSectionNonReturning = currentTypeSection[OUTPUTS_OFFSET] == NON_RETURNING;
            // If the section is non–returning, an exit is already implied.
            bool hasRequiredSectionExit = isCurrentSectionNonReturning;

            int position = 0;
            Instruction opcode = Instruction.STOP;

            while (position < code.Length)
            {
                opcode = (Instruction)code[position];
                int nextPosition = position + 1;

                // Check for undefined opcodes in the EOF context.
                if (!opcode.IsValid(isEofContext: true))
                {
                    if (Logger.IsTrace)
                        Logger.Trace($"EOF: Eof{VERSION}, CodeSection contains undefined opcode {opcode}");
                    return false;
                }
                else if (opcode is Instruction.RETURN or Instruction.STOP)
                {
                    // RETURN/STOP are disallowed in InitCode mode.
                    if (strategy.HasFlag(ValidationStrategy.ValidateInitCodeMode))
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, CodeSection contains {opcode} opcode");
                        return false;
                    }
                    else
                    {
                        // If the container has already been marked for InitCode mode, RETURN/STOP is not allowed.
                        if (containersWorklist.VisitedContainers[0] == ValidationStrategy.ValidateInitCodeMode)
                        {
                            if (Logger.IsTrace)
                                Logger.Trace($"EOF: Eof{VERSION}, CodeSection cannot contain {opcode} opcode");
                            return false;
                        }
                        else
                        {
                            containersWorklist.VisitedContainers[0] = ValidationStrategy.ValidateRuntimeMode;
                        }
                    }
                }
                else if (opcode == Instruction.RETURNCODE)
                {
                    // Validate the RETURNCODE branch.
                    if (!ValidateReturnCode(ref nextPosition, strategy, containersWorklist, eofContainer, code, invalidJumpDestinations))
                        return false;
                }
                else if (opcode is Instruction.RJUMP or Instruction.RJUMPI)
                {
                    // Validate relative jump instructions.
                    if (!ValidateRelativeJump(ref nextPosition, opcode, code, jumpDestinations, invalidJumpDestinations))
                        return false;
                }
                else if (opcode == Instruction.JUMPF)
                {
                    if (nextPosition + TWO_BYTE_LENGTH > code.Length)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, {Instruction.JUMPF} Argument underflow");
                        return false;
                    }

                    ushort targetSectionId = code.Slice(nextPosition, TWO_BYTE_LENGTH).ReadEthUInt16();

                    if (targetSectionId >= eofContainer.Header.CodeSections.Count)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, {Instruction.JUMPF} to unknown code section");
                        return false;
                    }

                    ReadOnlySpan<byte> targetTypeSection = eofContainer.TypeSections[targetSectionId].Span;
                    byte targetSectionOutputCount = targetTypeSection[OUTPUTS_OFFSET];
                    bool isTargetSectionNonReturning = targetTypeSection[OUTPUTS_OFFSET] == NON_RETURNING;
                    byte currentSectionOutputCount = currentTypeSection[OUTPUTS_OFFSET];

                    // Check that the jump target does not require more outputs than the current section.
                    if (!isTargetSectionNonReturning && currentSectionOutputCount < targetSectionOutputCount)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, {Instruction.JUMPF} to code section with more outputs");
                        return false;
                    }

                    // Non–returning sections must only jump to other non–returning sections.
                    if (isCurrentSectionNonReturning && !isTargetSectionNonReturning)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, {Instruction.JUMPF} from non-returning must target non-returning");
                        return false;
                    }

                    // JUMPF is only returnig when the target is returning
                    if (!isTargetSectionNonReturning)
                    {
                        hasRequiredSectionExit = true;
                    }

                    sectionsWorklist.Enqueue(targetSectionId, strategy);
                    BitmapHelper.FlagMultipleBits(TWO_BYTE_LENGTH, invalidJumpDestinations, ref nextPosition);
                }
                else if (opcode is Instruction.DUPN or Instruction.SWAPN or Instruction.EXCHANGE)
                {
                    if (nextPosition + ONE_BYTE_LENGTH > code.Length)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, {opcode.FastToString()} Argument underflow");
                        return false;
                    }
                    BitmapHelper.FlagMultipleBits(ONE_BYTE_LENGTH, invalidJumpDestinations, ref nextPosition);
                }
                else if (opcode == Instruction.RJUMPV)
                {
                    // Validate the relative jump table.
                    if (!ValidateRelativeJumpV(ref nextPosition, code, jumpDestinations, invalidJumpDestinations))
                        return false;
                }
                else if (opcode == Instruction.CALLF)
                {
                    // Validate the CALLF instruction.
                    if (!ValidateCallF(ref nextPosition, eofContainer, sectionsWorklist, strategy, code, invalidJumpDestinations))
                        return false;
                }
                else if (opcode == Instruction.RETF)
                {
                    // RETF indicates a proper exit from a section. Non–returning sections are not allowed to use RETF.
                    hasRequiredSectionExit = true;
                    if (isCurrentSectionNonReturning)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, non returning sections are not allowed to use opcode {Instruction.RETF}");
                        return false;
                    }
                }
                else if (opcode == Instruction.DATALOADN)
                {
                    // Validate that the data offset is within the data section bounds.
                    if (!ValidateDataLoadN(ref nextPosition, eofContainer, code, invalidJumpDestinations))
                        return false;
                }
                else if (opcode == Instruction.EOFCREATE)
                {
                    // Validate the EOFCREATE instruction.
                    if (!ValidateEofCreate(ref nextPosition, eofContainer, containersWorklist, code, invalidJumpDestinations))
                        return false;
                }
                else if (opcode >= Instruction.PUSH0 && opcode <= Instruction.PUSH32)
                {
                    int pushDataLength = opcode - Instruction.PUSH0;
                    if (nextPosition + pushDataLength > code.Length)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, {opcode.FastToString()} PC Reached out of bounds");
                        return false;
                    }
                    BitmapHelper.FlagMultipleBits(pushDataLength, invalidJumpDestinations, ref nextPosition);
                }

                position = nextPosition;
            }

            if (position > code.Length)
            {
                if (Logger.IsTrace)
                    Logger.Trace($"EOF: Eof{VERSION}, PC Reached out of bounds");
                return false;
            }

            if (!opcode.IsTerminating())
            {
                if (Logger.IsTrace)
                    Logger.Trace($"EOF: Eof{VERSION}, Code section {sectionId} ends with a non-terminating opcode");
                return false;
            }

            if (!hasRequiredSectionExit)
            {
                if (Logger.IsTrace)
                    Logger.Trace($"EOF: Eof{VERSION}, Code section {sectionId} is returning and does not have a RETF or JUMPF");
                return false;
            }

            if (BitmapHelper.CheckCollision(invalidJumpDestinations, jumpDestinations))
            {
                if (Logger.IsTrace)
                    Logger.Trace($"EOF: Eof{VERSION}, Invalid Jump destination detected");
                return false;
            }

            if (!ValidateStackState(sectionId, code, eofContainer.TypeSection.Span))
            {
                if (Logger.IsTrace)
                    Logger.Trace($"EOF: Eof{VERSION}, Invalid Stack state");
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

    /// <summary>
    /// Validates the RETURNCODE instruction branch.
    /// This branch verifies that the container mode is switched properly and that the immediate argument is valid.
    /// </summary>
    /// <param name="pos">A reference to the current position pointer (advanced on success).</param>
    /// <param name="strategy">The current validation strategy.</param>
    /// <param name="containersWorklist">The container worklist queue.</param>
    /// <param name="eofContainer">The entire EOF container.</param>
    /// <param name="code">The entire code span.</param>
    /// <param name="invalidJumpDestinations">The bitmap tracking invalid jump locations.</param>
    /// <returns>True if the RETURNCODE branch is valid; otherwise, false.</returns>
    private static bool ValidateReturnCode(
        ref int pos,
        ValidationStrategy strategy,
        in QueueManager containersWorklist,
        in EofContainer eofContainer,
        ReadOnlySpan<byte> code,
        Span<byte> invalidJumpDestinations)
    {
        if (strategy.HasFlag(ValidationStrategy.ValidateRuntimeMode))
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, CodeSection contains {Instruction.RETURNCODE} opcode");
            return false;
        }
        else
        {
            if (containersWorklist.VisitedContainers[0] == ValidationStrategy.ValidateRuntimeMode)
            {
                if (Logger.IsTrace)
                    Logger.Trace($"EOF: Eof{VERSION}, CodeSection cannot contain {Instruction.RETURNCODE} opcode");
                return false;
            }
            else
            {
                containersWorklist.VisitedContainers[0] = ValidationStrategy.ValidateInitCodeMode;
            }
        }

        // Ensure there is at least one byte for the immediate argument.
        if (pos + ONE_BYTE_LENGTH > code.Length)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RETURNCODE} Argument underflow");
            return false;
        }

        ushort runtimeContainerId = code[pos];

        if (eofContainer.Header.ContainerSections is null || runtimeContainerId >= eofContainer.Header.ContainerSections.Value.Count)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RETURNCODE}'s immediate argument must be less than containerSection.Count i.e.: {eofContainer.Header.ContainerSections?.Count}");
            return false;
        }

        if (containersWorklist.VisitedContainers[runtimeContainerId + 1] != 0 &&
            containersWorklist.VisitedContainers[runtimeContainerId + 1] != ValidationStrategy.ValidateRuntimeMode)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RETURNCODE}'s target container can only be a runtime mode bytecode");
            return false;
        }

        containersWorklist.Enqueue(runtimeContainerId + 1, ValidationStrategy.ValidateRuntimeMode | ValidationStrategy.ValidateFullBody);
        BitmapHelper.FlagMultipleBits(ONE_BYTE_LENGTH, invalidJumpDestinations, ref pos);
        return true;
    }

    /// <summary>
    /// Validates a relative jump instruction (RJUMP or RJUMPI).
    /// Verifies that the two-byte immediate exists and that the computed destination is within code bounds.
    /// </summary>
    /// <param name="pos">A reference to the current position pointer (advanced on success).</param>
    /// <param name="opcode">The jump opcode being validated.</param>
    /// <param name="code">The entire code span.</param>
    /// <param name="jumpDestinations">The bitmap for valid jump destinations.</param>
    /// <param name="invalidJumpDestinations">The bitmap for invalid jump destinations.</param>
    /// <returns>True if the relative jump is valid; otherwise, false.</returns>
    private static bool ValidateRelativeJump(
        ref int pos,
        Instruction opcode,
        ReadOnlySpan<byte> code,
        Span<byte> jumpDestinations,
        Span<byte> invalidJumpDestinations)
    {
        if (pos + TWO_BYTE_LENGTH > code.Length)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {opcode.FastToString()} Argument underflow");
            return false;
        }

        short offset = code.Slice(pos, TWO_BYTE_LENGTH).ReadEthInt16();
        int rJumpDest = offset + TWO_BYTE_LENGTH + pos;

        if (rJumpDest < 0 || rJumpDest >= code.Length)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {opcode.FastToString()} Destination outside of Code bounds");
            return false;
        }

        BitmapHelper.FlagMultipleBits(ONE_BYTE_LENGTH, jumpDestinations, ref rJumpDest);
        BitmapHelper.FlagMultipleBits(TWO_BYTE_LENGTH, invalidJumpDestinations, ref pos);
        return true;
    }

    /// <summary>
    /// Validates the relative jump table instruction (RJUMPV).
    /// Ensures that the jump table exists, has at least one entry, and that every computed destination is within bounds.
    /// </summary>
    /// <param name="pos">A reference to the current position pointer (advanced on success).</param>
    /// <param name="code">The entire code span.</param>
    /// <param name="jumpDestinations">The bitmap for valid jump destinations.</param>
    /// <param name="invalidJumpDestinations">The bitmap for invalid jump destinations.</param>
    /// <returns>True if the RJUMPV instruction is valid; otherwise, false.</returns>
    private static bool ValidateRelativeJumpV(
        ref int pos,
        ReadOnlySpan<byte> code,
        Span<byte> jumpDestinations,
        Span<byte> invalidJumpDestinations)
    {
        if (pos + ONE_BYTE_LENGTH + TWO_BYTE_LENGTH > code.Length)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RJUMPV} Argument underflow");
            return false;
        }

        // The jump table length is encoded as immediate value + 1.
        ushort count = (ushort)(code[pos] + 1);
        if (count < MINIMUMS_ACCEPTABLE_JUMPV_JUMPTABLE_LENGTH)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RJUMPV} jumpTable must have at least 1 entry");
            return false;
        }

        if (pos + ONE_BYTE_LENGTH + count * TWO_BYTE_LENGTH > code.Length)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RJUMPV} jumpTable underflow");
            return false;
        }

        int immediateValueSize = ONE_BYTE_LENGTH + count * TWO_BYTE_LENGTH;

        for (int j = 0; j < count; j++)
        {
            short offset = code.Slice(pos + ONE_BYTE_LENGTH + j * TWO_BYTE_LENGTH, TWO_BYTE_LENGTH).ReadEthInt16();
            int rJumpDest = offset + immediateValueSize + pos;
            if (rJumpDest < 0 || rJumpDest >= code.Length)
            {
                if (Logger.IsTrace)
                    Logger.Trace($"EOF: Eof{VERSION}, {Instruction.RJUMPV} Destination outside of Code bounds");
                return false;
            }
            BitmapHelper.FlagMultipleBits(ONE_BYTE_LENGTH, jumpDestinations, ref rJumpDest);
        }

        BitmapHelper.FlagMultipleBits(immediateValueSize, invalidJumpDestinations, ref pos);
        return true;
    }

    /// <summary>
    /// Validates the CALLF instruction branch.
    /// Checks that the target code section exists, that its type section indicates a returning function,
    /// and that the jump table bits are handled appropriately.
    /// </summary>
    /// <param name="pos">A reference to the current position pointer (advanced on success).</param>
    /// <param name="eofContainer">The EOF container containing code and type sections.</param>
    /// <param name="sectionsWorklist">The queue manager for code sections.</param>
    /// <param name="strategy">The current validation strategy.</param>
    /// <param name="code">The entire code span.</param>
    /// <param name="invalidJumpDestinations">The bitmap for invalid jump destinations.</param>
    /// <returns>True if the CALLF branch is valid; otherwise, false.</returns>
    private static bool ValidateCallF(
        ref int pos,
        in EofContainer eofContainer,
        in QueueManager sectionsWorklist,
        ValidationStrategy strategy,
        ReadOnlySpan<byte> code,
        Span<byte> invalidJumpDestinations)
    {
        if (pos + TWO_BYTE_LENGTH > code.Length)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {Instruction.CALLF} Argument underflow");
            return false;
        }

        ushort targetSectionId = code.Slice(pos, TWO_BYTE_LENGTH).ReadEthUInt16();
        if (targetSectionId >= eofContainer.Header.CodeSections.Count)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {Instruction.CALLF} Invalid Section Id");
            return false;
        }

        ReadOnlySpan<byte> targetTypeSection = eofContainer.TypeSections[targetSectionId].Span;
        byte targetSectionOutputCount = targetTypeSection[OUTPUTS_OFFSET];

        if (targetSectionOutputCount == NON_RETURNING)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {Instruction.CALLF} into non-returning function");
            return false;
        }

        sectionsWorklist.Enqueue(targetSectionId, strategy);
        BitmapHelper.FlagMultipleBits(TWO_BYTE_LENGTH, invalidJumpDestinations, ref pos);
        return true;
    }

    /// <summary>
    /// Validates the DATALOADN instruction branch.
    /// Verifies that the two-byte immediate argument is within the bounds of the data section.
    /// </summary>
    /// <param name="pos">A reference to the current position pointer (advanced on success).</param>
    /// <param name="eofContainer">The EOF container holding the data section.</param>
    /// <param name="code">The entire code span.</param>
    /// <param name="invalidJumpDestinations">The bitmap for invalid jump destinations.</param>
    /// <returns>True if the DATALOADN branch is valid; otherwise, false.</returns>
    private static bool ValidateDataLoadN(
        ref int pos,
        in EofContainer eofContainer,
        ReadOnlySpan<byte> code,
        Span<byte> invalidJumpDestinations)
    {
        if (pos + TWO_BYTE_LENGTH > code.Length)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {Instruction.DATALOADN} Argument underflow");
            return false;
        }

        ushort dataSectionOffset = code.Slice(pos, TWO_BYTE_LENGTH).ReadEthUInt16();
        if (dataSectionOffset + 32 > eofContainer.Header.DataSection.Size)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {Instruction.DATALOADN}'s immediate argument must be less than dataSection.Length / 32 i.e.: {eofContainer.Header.DataSection.Size / 32}");
            return false;
        }

        BitmapHelper.FlagMultipleBits(TWO_BYTE_LENGTH, invalidJumpDestinations, ref pos);
        return true;
    }

    /// <summary>
    /// Validates the EOFCREATE instruction branch.
    /// Ensures that the immediate argument is within the valid range for container sections
    /// and that the target container is in the proper mode.
    /// </summary>
    /// <param name="pos">A reference to the current position pointer (advanced on success).</param>
    /// <param name="eofContainer">The EOF container containing container sections.</param>
    /// <param name="containersWorklist">The container worklist queue.</param>
    /// <param name="code">The entire code span.</param>
    /// <param name="invalidJumpDestinations">The bitmap for invalid jump destinations.</param>
    /// <returns>True if the EOFCREATE branch is valid; otherwise, false.</returns>
    private static bool ValidateEofCreate(
        ref int pos,
        in EofContainer eofContainer,
        in QueueManager containersWorklist,
        ReadOnlySpan<byte> code,
        Span<byte> invalidJumpDestinations)
    {
        if (pos + ONE_BYTE_LENGTH > code.Length)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {Instruction.EOFCREATE} Argument underflow");
            return false;
        }

        int initCodeSectionId = code[pos];
        if (eofContainer.Header.ContainerSections is null || initCodeSectionId >= eofContainer.Header.ContainerSections.Value.Count)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {Instruction.EOFCREATE}'s immediate must fall within the Containers' range available, i.e.: {eofContainer.Header.CodeSections.Count}");
            return false;
        }

        if (containersWorklist.VisitedContainers[initCodeSectionId + 1] != 0 &&
            containersWorklist.VisitedContainers[initCodeSectionId + 1] != ValidationStrategy.ValidateInitCodeMode)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, {Instruction.EOFCREATE}'s target container can only be a initCode mode bytecode");
            return false;
        }

        containersWorklist.Enqueue(initCodeSectionId + 1, ValidationStrategy.ValidateInitCodeMode | ValidationStrategy.ValidateFullBody);
        BitmapHelper.FlagMultipleBits(ONE_BYTE_LENGTH, invalidJumpDestinations, ref pos);
        return true;
    }

    /// <summary>
    /// Validates the stack state for a given section of bytecode.
    /// This method checks that the code’s instructions maintain a valid stack height
    /// and that all control flow paths (calls, jumps, returns) yield a consistent stack state.
    /// </summary>
    /// <param name="sectionId">The identifier for the section being validated.</param>
    /// <param name="code">The bytecode instructions to validate.</param>
    /// <param name="typeSection">
    /// A section of type metadata containing input/output stack requirements and maximum stack height constraints
    /// for each section.
    /// </param>
    /// <returns>True if the stack state is valid; otherwise, false.</returns>
    public static bool ValidateStackState(int sectionId, ReadOnlySpan<byte> code, ReadOnlySpan<byte> typeSection)
    {
        // Rent an array to record the stack bounds at each code offset.
        StackBounds[] recordedStackHeight = ArrayPool<StackBounds>.Shared.Rent(code.Length);
        Array.Fill(recordedStackHeight, new StackBounds(min: 1023, max: -1));

        try
        {
            // Get the suggested maximum stack height for this section.
            ushort suggestedMaxHeight = typeSection
                .Slice(sectionId * MINIMUM_TYPESECTION_SIZE + TWO_BYTE_LENGTH, TWO_BYTE_LENGTH)
                .ReadEthUInt16();

            // Determine the output count for this section. A value of NON_RETURNING indicates non-returning.
            ushort currentSectionOutputs = typeSection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET] == NON_RETURNING
                ? (ushort)0
                : typeSection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];

            // The initial stack height is determined by the number of inputs.
            short peakStackHeight = typeSection[sectionId * MINIMUM_TYPESECTION_SIZE + INPUTS_OFFSET];

            int unreachedBytes = code.Length;
            int programCounter = 0;
            // Initialize the recorded stack bounds for the starting instruction.
            recordedStackHeight[0] = new(peakStackHeight, peakStackHeight);
            StackBounds currentStackBounds = recordedStackHeight[0];

            while (programCounter < code.Length)
            {
                Instruction opcode = (Instruction)code[programCounter];
                (ushort inputCount, ushort outputCount, ushort immediateCount) = opcode.StackRequirements();

                int posPostInstruction = programCounter + 1;
                if (posPostInstruction > code.Length)
                {
                    if (Logger.IsTrace)
                        Logger.Trace($"EOF: Eof{VERSION}, PC Reached out of bounds");
                    return false;
                }

                bool isTargetSectionNonReturning = false;

                // Apply opcode-specific modifications for opcodes that carry immediate data.
                if (opcode is Instruction.CALLF or Instruction.JUMPF or Instruction.DUPN or Instruction.SWAPN or Instruction.EXCHANGE)
                {
                    try
                    {
                        InstructionModificationResult mod = ApplyOpcodeImmediateModifiers(opcode, posPostInstruction, currentSectionOutputs, immediateCount, currentStackBounds, code, typeSection);
                        inputCount = mod.InputCount;
                        outputCount = mod.OutputCount;
                        immediateCount = mod.ImmediateCount;
                        isTargetSectionNonReturning = mod.IsTargetSectionNonReturning;
                    }
                    catch (InvalidOperationException)
                    {
                        // The helper methods throw on validation errors.
                        return false;
                    }
                }

                // Check for stack underflow.
                if ((isTargetSectionNonReturning || opcode is not Instruction.JUMPF) && currentStackBounds.Min < inputCount)
                {
                    if (Logger.IsTrace)
                        Logger.Trace($"EOF: Eof{VERSION}, Stack Underflow required {inputCount} but found {currentStackBounds.Min}");
                    return false;
                }

                // For non-terminating instructions, adjust the current stack bounds.
                if (!opcode.IsTerminating())
                {
                    short delta = (short)(outputCount - inputCount);
                    currentStackBounds = new((short)(currentStackBounds.Min + delta), (short)(currentStackBounds.Max + delta));
                }
                peakStackHeight = Math.Max(peakStackHeight, currentStackBounds.Max);

                // Process control-flow opcodes.
                if (opcode == Instruction.RETF)
                {
                    if (!ValidateReturnInstruction(sectionId, currentStackBounds, typeSection))
                        return false;
                }
                else if (opcode is Instruction.RJUMP or Instruction.RJUMPI)
                {
                    if (!ValidateRelativeJumpInstruction(opcode, programCounter, posPostInstruction, immediateCount, currentStackBounds, recordedStackHeight, code))
                        return false;
                }
                else if (opcode == Instruction.RJUMPV)
                {
                    if (!ValidateRelativeJumpVInstruction(programCounter, posPostInstruction, currentStackBounds, recordedStackHeight, code, out immediateCount, out posPostInstruction))
                        return false;
                }

                unreachedBytes -= 1 + immediateCount;
                programCounter += 1 + immediateCount;

                if (programCounter < code.Length)
                {
                    // Propagate recorded stack bounds for subsequent instructions.
                    if (opcode.IsTerminating())
                    {
                        ref StackBounds recordedBounds = ref recordedStackHeight[programCounter];
                        if (recordedBounds.Max < 0)
                        {
                            if (Logger.IsTrace)
                                Logger.Trace($"EOF: Eof{VERSION}, opcode not forward referenced, section {sectionId} pc {programCounter}");
                            return false;
                        }
                        currentStackBounds = recordedBounds;
                    }
                    else
                    {
                        ref StackBounds recordedBounds = ref recordedStackHeight[programCounter];
                        recordedBounds.Combine(currentStackBounds);
                        currentStackBounds = recordedBounds;
                    }
                }
            }

            if (unreachedBytes != 0)
            {
                if (Logger.IsTrace)
                    Logger.Trace($"EOF: Eof{VERSION}, bytecode has unreachable segments");
                return false;
            }

            if (peakStackHeight != suggestedMaxHeight)
            {
                if (Logger.IsTrace)
                    Logger.Trace($"EOF: Eof{VERSION}, Suggested Max Stack height mismatches with actual Max, expected {suggestedMaxHeight} but found {peakStackHeight}");
                return false;
            }

            if (peakStackHeight >= MAX_STACK_HEIGHT)
            {
                if (Logger.IsTrace)
                    Logger.Trace($"EOF: Eof{VERSION}, stack overflow exceeded max stack height of {MAX_STACK_HEIGHT} but found {peakStackHeight}");
                return false;
            }

            return true;
        }
        finally
        {
            ArrayPool<StackBounds>.Shared.Return(recordedStackHeight);
        }
    }

    /// <summary>
    /// Holds the result from adjusting an opcode’s immediate data.
    /// </summary>
    private struct InstructionModificationResult
    {
        public ushort InputCount;
        public ushort OutputCount;
        public ushort ImmediateCount;
        public bool IsTargetSectionNonReturning;
    }

    /// <summary>
    /// Adjusts the stack requirements for opcodes that carry immediate data.
    /// For CALLF and JUMPF the target section information is read from the immediate bytes,
    /// and additional validation is performed.
    /// For DUPN, SWAPN, and EXCHANGE the immediate value adjusts the input/output counts.
    /// </summary>
    /// <param name="opcode">The current opcode.</param>
    /// <param name="posPostInstruction">The code offset immediately after the opcode.</param>
    /// <param name="currentSectionOutputs">The output count for the current section.</param>
    /// <param name="immediateCount">The base immediate count from the opcode’s stack requirements.</param>
    /// <param name="currentStackBounds">The current stack bounds.</param>
    /// <param name="code">The full bytecode.</param>
    /// <param name="typeSection">The type section metadata.</param>
    /// <returns>A structure with the adjusted stack counts and immediate count.</returns>
    /// <exception cref="InvalidOperationException">Thrown if validation fails.</exception>
    private static InstructionModificationResult ApplyOpcodeImmediateModifiers(
        Instruction opcode,
        int posPostInstruction,
        ushort currentSectionOutputs,
        ushort immediateCount,
        in StackBounds currentStackBounds,
        ReadOnlySpan<byte> code,
        ReadOnlySpan<byte> typeSection)
    {
        var result = new InstructionModificationResult { ImmediateCount = immediateCount, IsTargetSectionNonReturning = false };

        switch (opcode)
        {
            case Instruction.CALLF:
            case Instruction.JUMPF:
                {
                    // Read the target section identifier from the immediate bytes.
                    ushort targetSectionId = code.Slice(posPostInstruction, immediateCount).ReadEthUInt16();
                    result.InputCount = typeSection[targetSectionId * MINIMUM_TYPESECTION_SIZE + INPUTS_OFFSET];
                    result.OutputCount = typeSection[targetSectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
                    result.IsTargetSectionNonReturning = result.OutputCount == NON_RETURNING;
                    if (result.IsTargetSectionNonReturning)
                    {
                        result.OutputCount = 0;
                    }
                    // Retrieve the maximum stack height allowed for the target section.
                    int targetMaxStackHeight = typeSection
                        .Slice(targetSectionId * MINIMUM_TYPESECTION_SIZE + MAX_STACK_HEIGHT_OFFSET, TWO_BYTE_LENGTH)
                        .ReadEthUInt16();
                    // Validate the stack height against the global maximum.
                    if (MAX_STACK_HEIGHT - targetMaxStackHeight + result.InputCount < currentStackBounds.Max)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, stack head during callF must not exceed {MAX_STACK_HEIGHT}");
                        throw new InvalidOperationException("Stack height exceeded in CALLF/JUMPF");
                    }
                    // For JUMPF (when returning) the stack state must match expected values.
                    if (opcode == Instruction.JUMPF && !result.IsTargetSectionNonReturning &&
                        !(currentSectionOutputs + result.InputCount - result.OutputCount == currentStackBounds.Min && currentStackBounds.BoundsEqual()))
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"EOF: Eof{VERSION}, Stack State invalid, required height {currentSectionOutputs + result.InputCount - result.OutputCount} but found {currentStackBounds.Max}");
                        throw new InvalidOperationException("Invalid stack state in JUMPF");
                    }
                    break;
                }
            case Instruction.DUPN:
                {
                    int imm_n = 1 + code[posPostInstruction];
                    result.InputCount = (ushort)imm_n;
                    result.OutputCount = (ushort)(result.InputCount + 1);
                    break;
                }
            case Instruction.SWAPN:
                {
                    int imm_n = 1 + code[posPostInstruction];
                    result.InputCount = result.OutputCount = (ushort)(1 + imm_n);
                    break;
                }
            case Instruction.EXCHANGE:
                {
                    int imm_n = 1 + (code[posPostInstruction] >> 4);
                    int imm_m = 1 + (code[posPostInstruction] & 0x0F);
                    result.InputCount = result.OutputCount = (ushort)(imm_n + imm_m + 1);
                    break;
                }
            default:
                throw new NotSupportedException("Opcode does not require immediate modifier adjustments.");
        }

        return result;
    }

    /// <summary>
    /// Validates the RETF instruction by checking that the current stack state exactly matches
    /// the expected output count for the section.
    /// </summary>
    /// <param name="sectionId">The identifier of the current section.</param>
    /// <param name="currentStackBounds">The current stack bounds.</param>
    /// <param name="typeSection">The type section metadata.</param>
    /// <returns>True if the RETF instruction’s requirements are met; otherwise, false.</returns>
    private static bool ValidateReturnInstruction(int sectionId, in StackBounds currentStackBounds, ReadOnlySpan<byte> typeSection)
    {
        int expectedHeight = typeSection[sectionId * MINIMUM_TYPESECTION_SIZE + OUTPUTS_OFFSET];
        if (expectedHeight != currentStackBounds.Min || !currentStackBounds.BoundsEqual())
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Stack state invalid required height {expectedHeight} but found {currentStackBounds.Min}");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Validates relative jump instructions (RJUMP and RJUMPI).
    /// Reads the jump offset, computes the destination and updates the recorded stack state as needed.
    /// </summary>
    /// <param name="opcode">The current opcode (RJUMP or RJUMPI).</param>
    /// <param name="programCounter">The current program counter.</param>
    /// <param name="posPostInstruction">The offset immediately after the opcode.</param>
    /// <param name="immediateCount">The immediate count associated with the opcode.</param>
    /// <param name="currentStackBounds">The current stack bounds.</param>
    /// <param name="recordedStackHeight">The array of recorded stack bounds per code offset.</param>
    /// <param name="code">The full bytecode.</param>
    /// <returns>True if the jump destination’s stack state is valid; otherwise, false.</returns>
    private static bool ValidateRelativeJumpInstruction(
        Instruction opcode,
        int programCounter,
        int posPostInstruction,
        ushort immediateCount,
        in StackBounds currentStackBounds,
        StackBounds[] recordedStackHeight,
        ReadOnlySpan<byte> code)
    {
        // Read the jump offset from the immediate bytes.
        short offset = code.Slice(programCounter + 1, immediateCount).ReadEthInt16();
        int jumpDestination = posPostInstruction + immediateCount + offset;

        // For RJUMPI, record the current stack state after the immediate data.
        if (opcode == Instruction.RJUMPI && (posPostInstruction + immediateCount < recordedStackHeight.Length))
            recordedStackHeight[posPostInstruction + immediateCount].Combine(currentStackBounds);

        return ValidateJumpDestination(jumpDestination, programCounter, currentStackBounds, recordedStackHeight);
    }

    /// <summary>
    /// Validates the RJUMPV instruction (relative jump vector).
    /// Reads the jump vector, validates each jump destination and returns updated immediate count and position.
    /// </summary>
    /// <param name="programCounter">The current program counter.</param>
    /// <param name="posPostInstruction">The offset immediately after the opcode.</param>
    /// <param name="currentStackBounds">The current stack bounds.</param>
    /// <param name="recordedStackHeight">The array of recorded stack bounds per code offset.</param>
    /// <param name="code">The full bytecode.</param>
    /// <param name="updatedImmediateCount">The updated immediate count after processing the jump vector.</param>
    /// <param name="updatedPosPostInstruction">The updated position after the jump vector.</param>
    /// <returns>True if all jump destinations in the vector are valid; otherwise, false.</returns>
    private static bool ValidateRelativeJumpVInstruction(
        int programCounter,
        int posPostInstruction,
        in StackBounds currentStackBounds,
        StackBounds[] recordedStackHeight,
        ReadOnlySpan<byte> code,
        out ushort updatedImmediateCount,
        out int updatedPosPostInstruction)
    {
        int count = code[posPostInstruction] + 1;
        updatedImmediateCount = (ushort)(count * TWO_BYTE_LENGTH + ONE_BYTE_LENGTH);

        // Validate each jump destination in the jump vector.
        for (short j = 0; j < count; j++)
        {
            int casePosition = posPostInstruction + ONE_BYTE_LENGTH + j * TWO_BYTE_LENGTH;
            int offset = code.Slice(casePosition, TWO_BYTE_LENGTH).ReadEthInt16();
            int jumpDestination = posPostInstruction + updatedImmediateCount + offset;
            if (!ValidateJumpDestination(jumpDestination, programCounter, currentStackBounds, recordedStackHeight))
            {
                updatedPosPostInstruction = posPostInstruction;
                return false;
            }
        }

        updatedPosPostInstruction = posPostInstruction + updatedImmediateCount;
        if (updatedPosPostInstruction > code.Length)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, PC Reached out of bounds");
            return false;
        }
        return true;
    }


    /// <summary>
    /// Validates the recorded stack bounds at a given jump destination.
    /// For forward jumps the current stack state is combined with the destination’s state;
    /// for backward jumps the destination’s recorded state must exactly match the current state.
    /// </summary>
    /// <param name="jumpDestination">The target code offset for the jump.</param>
    /// <param name="programCounter">The current program counter.</param>
    /// <param name="currentStackBounds">The current stack bounds.</param>
    /// <param name="recordedStackHeight">The array of recorded stack bounds per code offset.</param>
    /// <returns>True if the destination’s stack state is valid; otherwise, false.</returns>
    private static bool ValidateJumpDestination(
        int jumpDestination,
        int programCounter,
        in StackBounds currentStackBounds,
        StackBounds[] recordedStackHeight)
    {
        ref StackBounds recordedBounds = ref recordedStackHeight[jumpDestination];
        if (jumpDestination > programCounter)
        {
            recordedBounds.Combine(currentStackBounds);
        }
        else if (recordedBounds != currentStackBounds)
        {
            if (Logger.IsTrace)
                Logger.Trace($"EOF: Eof{VERSION}, Stack state invalid at {jumpDestination}");
            return false;
        }
        return true;
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly struct QueueManager(int containerCount)
    {
        public readonly Queue<(int index, ValidationStrategy strategy)> ContainerQueue = new();
        public readonly ValidationStrategy[] VisitedContainers = new ValidationStrategy[containerCount];

        public void Enqueue(int index, ValidationStrategy strategy) => ContainerQueue.Enqueue((index, strategy));

        public void MarkVisited(int index, ValidationStrategy strategy) => VisitedContainers[index] |= strategy;

        public bool TryDequeue(out (int Index, ValidationStrategy Strategy) worklet) => ContainerQueue.TryDequeue(out worklet);

        public bool IsAllVisitedAndNotAmbiguous() => VisitedContainers.All(validation =>
        {
            bool isEofCreate = validation.HasFlag(ValidationStrategy.InitCodeMode);
            bool isReturnCode = validation.HasFlag(ValidationStrategy.RuntimeMode);

            // Should be referenced but not by both EofCreate and ReturnCode.
            return validation != 0 && !(isEofCreate && isReturnCode);
        });
    }

    [StructLayout(LayoutKind.Auto)]
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

[StructLayout(LayoutKind.Auto)]
internal readonly struct StackBounds(short min, short max)
{
    public readonly short Min = min;
    public readonly short Max = max;

    public readonly bool BoundsEqual() => Max == Min;

    public static bool operator ==(in StackBounds left, in StackBounds right) => left.Max == right.Max && right.Min == left.Min;
    public static bool operator !=(in StackBounds left, in StackBounds right) => !(left == right);
    public override readonly bool Equals(object obj) => obj is StackBounds bounds && this == bounds;
    public override readonly int GetHashCode() => (Max << 16) | (int)Min;
}

file static class StackBoundsExtensions
{
    public static void Combine(this ref StackBounds bounds, StackBounds other)
    {
        bounds = new(Math.Min(bounds.Min, other.Min), Math.Max(bounds.Max, other.Max));
    }
}
