// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Logging;
using Nethermind.OpcodeTracing.Plugin.Tracing;

namespace Nethermind.OpcodeTracing.Plugin;

/// <summary>
/// Nethermind plugin for tracing opcode usage across block ranges.
/// </summary>
public class OpcodeTracingPlugin(IOpcodeTracingConfig config) : INethermindPlugin, IAsyncDisposable
{
    private readonly IOpcodeTracingConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly string _sessionId = GenerateSessionId();
    private OpcodeTraceRecorder? _traceRecorder;
    private ILogger? _logger;
    private INethermindApi? _api;

    /// <summary>
    /// Generates a unique session identifier for RealTime mode cumulative file naming per FR-071.
    /// </summary>
    private static string GenerateSessionId()
    {
        return DateTime.UtcNow.ToString("yyyyMMddHHmmss");
    }

    /// <summary>
    /// Gets the plugin name.
    /// </summary>
    public string Name => "Opcode tracing";

    /// <summary>
    /// Gets the plugin description.
    /// </summary>
    public string Description => "Traces EVM opcode usage across block ranges with configurable output modes";

    /// <summary>
    /// Gets the plugin author.
    /// </summary>
    public string Author => "Nethermind";

    /// <summary>
    /// Gets a value indicating whether the plugin is enabled.
    /// </summary>
    public bool Enabled => _config.Enabled;

    /// <summary>
    /// Gets a value indicating whether the plugin must initialize.
    /// </summary>
    public bool MustInitialize => false;

    /// <summary>
    /// Initializes the plugin.
    /// </summary>
    /// <param name="nethermindApi">The Nethermind API.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi ?? throw new ArgumentNullException(nameof(nethermindApi));
        _logger = _api.LogManager.GetClassLogger<OpcodeTracingPlugin>();

        if (!Enabled)
        {
            return;
        }

        try
        {
            // Initialize dependencies from DI container or create them directly
            var counter = new OpcodeCounter();
            var outputWriter = new Output.TraceOutputWriter(_api.LogManager);
            _traceRecorder = new OpcodeTraceRecorder(_config, counter, outputWriter, _sessionId, _api.LogManager);

            await _traceRecorder.PrepareAsync(_api).ConfigureAwait(false);

            _logger?.Info($"Opcode tracing plugin initialized (session={_sessionId}).");

            // Provide guidance for RealTime mode users
            if (string.Equals(_config.Mode, "RealTime", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.Info(
                    "RealTime mode: Tracing will begin automatically when the node reaches the chain tip. " +
                    "Please wait for sync to complete - look for 'RealTime opcode tracing is now active' message.");
            }
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to initialize opcode tracing plugin: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// Initializes the network protocol integration.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task InitNetworkProtocol()
    {
        if (!Enabled || _api is null || _traceRecorder is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            // For RealTime mode, attach to block processor
            _traceRecorder.Attach(_api);

            // For Retrospective mode, start the tracing task
            _ = _traceRecorder.ExecuteTracingAsync(_api);

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to initialize network protocol: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// Gets the Autofac module for dependency injection.
    /// </summary>
    public IModule Module => new OpcodeTracingModule();

    /// <summary>
    /// Asynchronously disposes of the plugin resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _logger?.Info("Disposing opcode tracing plugin...");

        if (_traceRecorder is not null)
        {
            await _traceRecorder.DisposeAsync().ConfigureAwait(false);
        }
    }
}
