// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging
{
    public interface ILogger
    {
        void Info(string text);
        void Warn(string text);
        void Debug(string text);
        void Trace(string text);
        void Error(string text, Exception ex = null);

        bool IsInfo { get; }
        bool IsWarn { get; }
        bool IsDebug { get; }
        bool IsTrace { get; }
        bool IsError { get; }
    }
}
