// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Logging;

namespace Nethermind.OpcodeTracing.Plugin.Output;

/// <summary>
/// Writes per-block opcode trace output to individual JSON files.
/// Files are named with pattern opcode-trace-block-{blockNumber}.json.
/// </summary>
public sealed class PerBlockTraceWriter
{
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="PerBlockTraceWriter"/> class.
    /// </summary>
    /// <param name="logManager">The log manager.</param>
    public PerBlockTraceWriter(ILogManager logManager)
    {
        _logger = logManager?.GetClassLogger<PerBlockTraceWriter>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    /// <summary>
    /// Writes a per-block trace output to a JSON file asynchronously.
    /// Target latency is under 100ms.
    /// </summary>
    /// <param name="outputDirectory">The output directory path.</param>
    /// <param name="traceOutput">The per-block trace output to write.</param>
    /// <returns>The full path to the created file, or null if writing failed.</returns>
    public async Task<string?> WriteBlockAsync(string outputDirectory, PerBlockTraceOutput traceOutput)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            if (_logger.IsError)
            {
                _logger.Error("Output directory is null or empty");
            }
            return null;
        }

        if (traceOutput is null)
        {
            if (_logger.IsError)
            {
                _logger.Error("Per-block trace output is null");
            }
            return null;
        }

        try
        {
            // Ensure directory exists
            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            // Generate filename: opcode-trace-block-{blockNumber}.json
            string fileName = $"opcode-trace-block-{traceOutput.Metadata.BlockNumber}.json";
            string filePath = Path.Combine(outputDirectory, fileName);

            // Serialize and write
            string json = JsonSerializer.Serialize(traceOutput, _serializerOptions);
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);

            if (_logger.IsDebug)
            {
                _logger.Debug($"Per-block opcode trace written for block {traceOutput.Metadata.BlockNumber}: {filePath}");
            }

            return filePath;
        }
        catch (Exception ex)
        {
            // Log error but continue processing
            if (_logger.IsError)
            {
                _logger.Error($"Failed to write per-block opcode trace for block {traceOutput.Metadata.BlockNumber}: {ex.Message}", ex);
            }
            return null;
        }
    }

}
