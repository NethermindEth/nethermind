// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging
{
    public interface InterfaceLogger
    {
        void Info(string text);
        void Warn(string text);
        void Debug(string text);
        void Trace(string text);
        void Error(string text, Exception ex = null);

        /// <summary>
        /// Logs a warning tagged with a <see cref="LogEventKind"/>. Sinks that support structured
        /// events (e.g. NLog → Seq) attach the kind as a queryable property so operators can
        /// filter these events separately from plain warnings. Default implementation drops the
        /// kind and falls through to <see cref="Warn(string)"/>.
        /// </summary>
        // Note: the sink may still suppress the event if its own level filter (IsWarn / IsError)
        // is not satisfied, even when IsDebug / IsTrace is true at the caller.
        void Warn(string text, LogEventKind kind) => Warn(text);

        /// <summary>
        /// Logs an error tagged with a <see cref="LogEventKind"/>. Sinks that support structured
        /// events (e.g. NLog → Seq) attach the kind as a queryable property so operators can
        /// filter these events separately from plain errors. Default implementation drops the
        /// kind and falls through to <see cref="Error(string, Exception)"/>.
        /// </summary>
        void Error(string text, LogEventKind kind, Exception ex = null) => Error(text, ex);

        bool IsInfo { get; }
        bool IsWarn { get; }
        bool IsDebug { get; }
        bool IsTrace { get; }
        bool IsError { get; }
    }
}
