// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Logging;

namespace Nethermind.OpcodeTracing.Plugin.Output;

/// <summary>
/// Writes opcode trace output to JSON files.
/// </summary>
public sealed class TraceOutputWriter
{
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="TraceOutputWriter"/> class.
    /// </summary>
    /// <param name="logManager">The log manager.</param>
    public TraceOutputWriter(ILogManager logManager)
    {
        _logger = logManager?.GetClassLogger<TraceOutputWriter>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    /// <summary>
    /// Writes a trace output to a JSON file.
    /// </summary>
    /// <param name="outputDirectory">The output directory path.</param>
    /// <param name="traceOutput">The trace output to write.</param>
    /// <returns>The full path to the created file, or null if writing failed.</returns>
    public async Task<string?> WriteAsync(string outputDirectory, TraceOutput traceOutput)
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
                _logger.Error("Trace output is null");
            }
            return null;
        }

        try
        {
            // Resolve path using PathUtils for proper relative/absolute path handling
            string resolvedDirectory = outputDirectory.GetApplicationResourcePath();

            // Ensure directory exists
            if (!Directory.Exists(resolvedDirectory))
            {
                Directory.CreateDirectory(resolvedDirectory);
            }

            // Generate filename based on block range
            string fileName = $"opcode-trace-{traceOutput.Metadata.StartBlock}-{traceOutput.Metadata.EndBlock}.json";
            string filePath = Path.Combine(resolvedDirectory, fileName);

            // Serialize directly to file stream
            await using FileStream stream = new(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
            await JsonSerializer.SerializeAsync(stream, traceOutput, _serializerOptions).ConfigureAwait(false);

            if (_logger.IsDebug)
            {
                _logger.Debug($"Opcode trace file created: {filePath}");
            }

            return filePath;
        }
        catch (Exception ex)
        {
            if (_logger.IsError)
            {
                _logger.Error($"Failed to write opcode trace: {ex.Message}", ex);
            }
            return null;
        }
    }
}
