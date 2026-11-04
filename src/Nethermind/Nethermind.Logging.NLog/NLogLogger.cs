// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NLog;

namespace Nethermind.Logging.NLog
{
    public class NLogLogger : InterfaceLogger
    {
        public bool IsError { get; }
        public bool IsWarn { get; }
        public bool IsInfo { get; }
        public bool IsDebug { get; }
        public bool IsTrace { get; }

        public string Name { get; }

        private readonly Logger _logger;

        public NLogLogger(Type type) : this(ILogManager.GetLoggerName(type))
        {
        }

        public NLogLogger(string loggerName)
        {
            _logger = LogManager.GetLogger(loggerName);

            /* NOTE: minor perf gain - not planning to switch logging levels while app is running */
            // TODO: review the behaviour on log levels switching
            IsInfo = _logger.IsInfoEnabled;
            IsWarn = _logger.IsWarnEnabled;
            IsDebug = _logger.IsDebugEnabled;
            IsTrace = _logger.IsTraceEnabled;
            IsError = _logger.IsErrorEnabled || _logger.IsFatalEnabled;
            Name = _logger.Name;
        }

        public void Info(string text)
        {
            if (IsInfo) _logger.Info(IpMasker.MaskIfEnabled(text));
        }

        public void Warn(string text)
        {
            if (IsWarn) _logger.Warn(IpMasker.MaskIfEnabled(text));
        }

        public void Debug(string text)
        {
            if (IsDebug) _logger.Debug(IpMasker.MaskIfEnabled(text));
        }

        public void Trace(string text)
        {
            if (IsTrace) _logger.Trace(IpMasker.MaskIfEnabled(text));
        }

        public void Error(string text, Exception? ex = null)
        {
            if (!IsError) return;
            (text, ex) = MaskIfEnabled(text, ex);
            _logger.Error(ex, text);
        }

        public void Warn(string text, LogEventKind kind)
        {
            if (!IsWarn) return;
            LogEventInfo evt = LogEventInfo.Create(global::NLog.LogLevel.Warn, _logger.Name, IpMasker.MaskIfEnabled(text));
            evt.Properties["Kind"] = Enum.GetName(kind);
            _logger.Log(evt);
        }

        public void Error(string text, LogEventKind kind, Exception? ex = null)
        {
            if (!IsError) return;
            (text, ex) = MaskIfEnabled(text, ex);
            LogEventInfo evt = LogEventInfo.Create(global::NLog.LogLevel.Error, _logger.Name, ex, null, text);
            evt.Properties["Kind"] = Enum.GetName(kind);
            _logger.Log(evt);
        }

        /// <remarks>
        /// Exception messages can carry addresses, e.g. a <see cref="System.Net.Sockets.SocketException"/> names the remote endpoint.
        /// The exception cannot be changed, so a substitute with the masked text replaces it.
        /// </remarks>
        private static (string Text, Exception? Ex) MaskIfEnabled(string text, Exception? ex) =>
            !IpMasker.Enabled ? (text, ex)
            : (IpMasker.Mask(text), ex is null ? null : new MaskedException(IpMasker.Mask(ex.Message), IpMasker.Mask(ex.ToString())));

        private sealed class MaskedException(string message, string text) : Exception(message)
        {
            public override string ToString() => text;
        }
    }
}
