// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Logging;

/// <summary>
/// Tag attached to log events that belong to a specific subcategory of their base severity.
/// Sinks that support structured properties (e.g. NLog → Seq) publish this as a queryable
/// <c>Kind</c> field, so operators can filter subcategories in/out of their dashboards without
/// relying on text patterns inside the log message.
/// </summary>
public enum LogEventKind
{
    /// <summary>
    /// An error that is only surfaced because debug-level logging is enabled. Not a real
    /// operational error on the node's main responsibilities. Emitted via
    /// <see cref="ILogger.DebugError"/>.
    /// </summary>
    DebugError,

    /// <summary>
    /// A warning that is only surfaced because debug-level logging is enabled. Emitted via
    /// <see cref="ILogger.DebugWarn"/>.
    /// </summary>
    DebugWarn,

    /// <summary>
    /// An error that is only surfaced because trace-level logging is enabled. Even lower
    /// signal than <see cref="DebugError"/>. Emitted via <see cref="ILogger.TraceError"/>.
    /// </summary>
    TraceError,

    /// <summary>
    /// A warning that is only surfaced because trace-level logging is enabled. Even lower
    /// signal than <see cref="DebugWarn"/>. Emitted via <see cref="ILogger.TraceWarn"/>.
    /// </summary>
    TraceWarn,
}
