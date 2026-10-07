// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using Microsoft.ClearScript;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

internal sealed class JavaScriptTraceFailure(Exception inner) : Exception(inner.Message, inner)
{
    internal static bool IsRecoverable(Exception exception, bool observedNullThrow = false)
    {
        if (exception is JavaScriptTraceFailure) return true;
        while (true)
        {
            switch (exception)
            {
                case ScriptInterruptedException:
                    return false;
                case ScriptEngineException script:
                    if (script.IsFatal) return false;
                    if (script.InnerException is null) return script.ScriptExceptionAsObject is not null || observedNullThrow;
                    exception = script.InnerException;
                    break;
                case TargetInvocationException { InnerException: { } inner }:
                    exception = inner;
                    break;
                default:
                    return false;
            }
        }
    }
}
