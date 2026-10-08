// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc;

/// <summary>
/// The engine API depends on the order its requests arrive in: a pipelined <c>newPayload</c> must run before the
/// <c>forkchoiceUpdated</c> that makes it head. Engine and authenticated connections process one request at a time.
/// </summary>
public static class JsonRpcProcessingConcurrency
{
    public static int ForUrl(JsonRpcUrl url, int configuredConcurrency) =>
        url.IsAuthenticated ? 1 : ForModules(url.EnabledModules, configuredConcurrency);

    /// <remarks>For a connection without a URL, such as IPC, which serves the modules enabled in <c>JsonRpc.EnabledModules</c>.</remarks>
    public static int ForModules(IEnumerable<string> enabledModules, int configuredConcurrency) =>
        enabledModules.Contains(ModuleType.Engine, StringComparer.OrdinalIgnoreCase) ? 1 : configuredConcurrency;
}
