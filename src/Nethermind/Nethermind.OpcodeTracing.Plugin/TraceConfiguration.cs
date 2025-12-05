// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.OpcodeTracing.Plugin;

/// <summary>
/// Validated configuration for an opcode tracing operation.
/// </summary>
public sealed record TraceConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TraceConfiguration"/> class.
    /// </summary>
    /// <param name="enabled">Whether the plugin is enabled.</param>
    /// <param name="outputDirectory">The output directory path.</param>
    /// <param name="startBlock">The start block number (nullable).</param>
    /// <param name="endBlock">The end block number (nullable).</param>
    /// <param name="blocks">The number of recent blocks to trace (nullable).</param>
    /// <param name="mode">The tracing mode.</param>
    /// <param name="maxDegreeOfParallelism">The maximum degree of parallelism.</param>
    public TraceConfiguration(
        bool enabled,
        string outputDirectory,
        long? startBlock,
        long? endBlock,
        long? blocks,
        TracingMode mode,
        int maxDegreeOfParallelism)
    {
        Enabled = enabled;
        OutputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
        StartBlock = startBlock;
        EndBlock = endBlock;
        Blocks = blocks;
        Mode = mode;
        MaxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    /// <summary>
    /// Gets a value indicating whether the plugin is enabled.
    /// </summary>
    public bool Enabled { get; }

    /// <summary>
    /// Gets the output directory path.
    /// </summary>
    public string OutputDirectory { get; }

    /// <summary>
    /// Gets the start block number (nullable).
    /// </summary>
    public long? StartBlock { get; }

    /// <summary>
    /// Gets the end block number (nullable).
    /// </summary>
    public long? EndBlock { get; }

    /// <summary>
    /// Gets the number of recent blocks to trace (nullable).
    /// </summary>
    public long? Blocks { get; }

    /// <summary>
    /// Gets the tracing mode.
    /// </summary>
    public TracingMode Mode { get; }

    /// <summary>
    /// Gets the maximum degree of parallelism.
    /// </summary>
    public int MaxDegreeOfParallelism { get; }

    /// <summary>
    /// Gets the effective start block after resolving configuration.
    /// </summary>
    public long EffectiveStartBlock { get; init; }

    /// <summary>
    /// Gets the effective end block after resolving configuration.
    /// </summary>
    public long EffectiveEndBlock { get; init; }

    /// <summary>
    /// Gets the list of warnings generated during configuration resolution.
    /// </summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>
    /// Creates a trace configuration from the plugin configuration.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="currentChainTip">The current chain tip block number.</param>
    /// <returns>A validated trace configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown when configuration is invalid.</exception>
    public static TraceConfiguration FromConfig(IOpcodeTracingConfig config, long currentChainTip)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        TracingMode mode = Enum.TryParse<TracingMode>(config.Mode, true, out var parsedMode)
            ? parsedMode
            : TracingMode.RealTime;

        var traceConfig = new TraceConfiguration(
            config.Enabled,
            config.OutputDirectory,
            config.StartBlock,
            config.EndBlock,
            config.Blocks,
            mode,
            config.MaxDegreeOfParallelism);

        // Resolve effective block range
        long effectiveStart;
        long effectiveEnd;
        var warnings = new List<string>();

        // Check for conflicting parameters
        if (config.StartBlock.HasValue && config.EndBlock.HasValue && config.Blocks.HasValue)
        {
            warnings.Add("Both explicit range (StartBlock/EndBlock) and Blocks specified. Using explicit range, ignoring Blocks.");
            effectiveStart = config.StartBlock.Value;
            effectiveEnd = config.EndBlock.Value;
        }
        else if (config.StartBlock.HasValue && config.EndBlock.HasValue)
        {
            // Explicit range
            effectiveStart = config.StartBlock.Value;
            effectiveEnd = config.EndBlock.Value;
        }
        else if (config.Blocks.HasValue)
        {
            // Recent N blocks
            effectiveEnd = currentChainTip;
            effectiveStart = Math.Max(0, currentChainTip - config.Blocks.Value + 1);

            if (effectiveStart == 0 && config.Blocks.Value > currentChainTip + 1)
            {
                warnings.Add($"Requested {config.Blocks.Value} blocks but only {currentChainTip + 1} available. Tracing all available blocks.");
            }
        }
        else if (config.StartBlock.HasValue)
        {
            // Start block only, use current chain tip as end
            effectiveStart = config.StartBlock.Value;
            effectiveEnd = currentChainTip;
        }
        else if (config.EndBlock.HasValue)
        {
            // End block only, use 0 as start
            effectiveStart = 0;
            effectiveEnd = config.EndBlock.Value;
            warnings.Add("Only EndBlock specified, using 0 as StartBlock.");
        }
        else
        {
            throw new InvalidOperationException("No block range specified. Provide StartBlock/EndBlock or Blocks parameter.");
        }

        return traceConfig with
        {
            EffectiveStartBlock = effectiveStart,
            EffectiveEndBlock = effectiveEnd,
            Warnings = warnings
        };
    }
}
