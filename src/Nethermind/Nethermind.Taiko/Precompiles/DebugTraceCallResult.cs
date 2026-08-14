// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Taiko.Precompiles;

/// <summary>
/// Lightweight DTO for the debug_traceCall default tracer response.
/// Only the fields we need — structLogs is ignored by the deserializer.
/// </summary>
public sealed class DebugTraceCallResult
{
    public long Gas { get; set; }
    public bool Failed { get; set; }
    public string ReturnValue { get; set; } = string.Empty;
}
