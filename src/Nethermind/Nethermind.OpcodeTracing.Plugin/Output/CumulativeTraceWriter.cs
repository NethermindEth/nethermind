// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Logging;

namespace Nethermind.OpcodeTracing.Plugin.Output;

/// <summary>
/// Writes cumulative opcode trace output to JSON files.
/// The cumulative file is updated in-place after each block.
/// </summary>
public sealed class CumulativeTraceWriter
{
    private readonly ILogger _logger;
    private readonly string _outputDirectory;
    private readonly string _sessionId;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true
    };

    private string? _filePath;

    /// <summary>
    /// Gets the full path to the cumulative file.
    /// </summary>
    public string? FilePath => _filePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="CumulativeTraceWriter"/> class.
    /// </summary>
    /// <param name="outputDirectory">The output directory path.</param>
    /// <param name="sessionId">The unique session identifier for filename.</param>
    /// <param name="logManager">The log manager.</param>
    public CumulativeTraceWriter(string outputDirectory, string sessionId, ILogManager logManager)
    {
        _outputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _logger = logManager?.GetClassLogger<CumulativeTraceWriter>() ?? throw new ArgumentNullException(nameof(logManager));

        // Pre-compute the file path - "all" sorts before "block" alphabetically
        _filePath = Path.Combine(_outputDirectory, $"opcode-trace-all-{_sessionId}.json");
    }

    /// <summary>
    /// Writes (or updates) the cumulative trace output to the JSON file.
    /// This overwrites the existing file per spec assumption.
    /// </summary>
    /// <param name="traceOutput">The cumulative trace output to write.</param>
    /// <returns>The full path to the file, or null if writing failed.</returns>
    public async Task<string?> WriteAsync(CumulativeTraceOutput traceOutput)
    {
        if (traceOutput is null)
        {
            if (_logger.IsError)
            {
                _logger.Error("Cumulative trace output is null");
            }
            return null;
        }

        try
        {
            // Ensure directory exists
            if (!Directory.Exists(_outputDirectory))
            {
                Directory.CreateDirectory(_outputDirectory);
            }

            // Serialize and write (overwrite existing file)
            string json = JsonSerializer.Serialize(traceOutput, _serializerOptions);
            await File.WriteAllTextAsync(_filePath!, json).ConfigureAwait(false);

            if (_logger.IsDebug)
            {
                _logger.Debug($"Cumulative opcode trace updated: {_filePath} (blocks {traceOutput.Metadata.FirstBlock}-{traceOutput.Metadata.LastBlock})");
            }

            return _filePath;
        }
        catch (Exception ex)
        {
            if (_logger.IsError)
            {
                _logger.Error($"Failed to write cumulative opcode trace: {ex.Message}", ex);
            }
            return null;
        }
    }

    /// <summary>
    /// Finalizes the cumulative file with the specified completion status.
    /// </summary>
    /// <param name="traceOutput">The final trace output.</param>
    /// <param name="completionStatus">The completion status ("complete" or "partial").</param>
    /// <returns>The full path to the file, or null if writing failed.</returns>
    public async Task<string?> FinalizeAsync(CumulativeTraceOutput traceOutput, string completionStatus)
    {
        if (traceOutput is null)
        {
            return null;
        }

        // Create a copy with the completion status set
        CumulativeTraceOutput finalOutput = new()
        {
            Metadata = new CumulativeMetadata
            {
                FirstBlock = traceOutput.Metadata.FirstBlock,
                LastBlock = traceOutput.Metadata.LastBlock,
                TotalBlocksProcessed = traceOutput.Metadata.TotalBlocksProcessed,
                SessionId = traceOutput.Metadata.SessionId,
                Mode = traceOutput.Metadata.Mode,
                StartedAt = traceOutput.Metadata.StartedAt,
                LastUpdatedAt = DateTime.UtcNow,
                Duration = traceOutput.Metadata.Duration,
                CompletionStatus = completionStatus,
                ConfiguredStartBlock = traceOutput.Metadata.ConfiguredStartBlock,
                ConfiguredEndBlock = traceOutput.Metadata.ConfiguredEndBlock,
                Warnings = traceOutput.Metadata.Warnings
            },
            OpcodeCounts = traceOutput.OpcodeCounts
        };

        string? result = await WriteAsync(finalOutput).ConfigureAwait(false);

        if (result is not null && _logger.IsInfo)
        {
            _logger.Info($"Cumulative opcode trace finalized with status '{completionStatus}': {result}");
        }

        return result;
    }
}
