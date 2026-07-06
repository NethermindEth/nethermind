// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api.Extensions;

namespace Nethermind.OpcodeTracing.Plugin;

/// <summary>
/// Nethermind plugin for tracing opcode usage across block ranges.
/// </summary>
public class OpcodeTracingPlugin(IOpcodeTracingConfig? config = null) : INethermindPlugin
{
    private IOpcodeTracingConfig? _config = config;

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
    public bool Enabled => _config?.Enabled ?? false;

    /// <summary>
    /// Gets a value indicating whether the plugin must initialize.
    /// </summary>
    public bool MustInitialize => false;

    /// <summary>
    /// Gets the Autofac module for dependency injection.
    /// </summary>
    public IModule Module => new OpcodeTracingModule(_config!);
}
