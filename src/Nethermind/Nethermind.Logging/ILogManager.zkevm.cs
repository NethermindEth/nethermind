// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Logging;

public partial interface ILogManager
{
    /// <summary>Gets the logger for <typeparamref name="T"/>.</summary>
    /// <remarks>
    /// Non-virtual in the zkEVM build: a generic virtual call makes the NativeAOT runtime build the target
    /// instantiation through the type loader on first use at every call site, and zkEVM logging is a no-op,
    /// so the logger name is irrelevant.
    /// </remarks>
    sealed ILogger GetClassLogger<T>() => GetLogger(string.Empty);
}
