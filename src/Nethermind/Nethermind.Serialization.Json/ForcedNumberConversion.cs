// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Serialization.Json;

public static class ForcedNumberConversion
{
    public static readonly ThreadAwareAsyncLocal ForcedConversion = new();

    [ThreadStatic]
    private static NumberConversion? _threadCache;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NumberConversion GetFinalConversion() => _threadCache ?? NumberConversion.Hex;

    /// <summary>
    /// Wrapper around AsyncLocal that also updates a ThreadStatic cache for fast reads.
    /// </summary>
    public sealed class ThreadAwareAsyncLocal
    {
        private readonly AsyncLocal<NumberConversion?> _asyncLocal = new();

        public NumberConversion? Value
        {
            get => _asyncLocal.Value;
            set
            {
                _asyncLocal.Value = value;
                _threadCache = value;
            }
        }
    }
}
