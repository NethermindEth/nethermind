// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Evm.EvmObjectFormat.Handlers;
using Nethermind.Logging;

[assembly: InternalsVisibleTo("Nethermind.EofParser")]
[assembly: InternalsVisibleTo("Ethereum.Test.Base")]

namespace Nethermind.Evm.EvmObjectFormat;

public static class EofValidator
{
    // magic prefix : EofFormatByte is the first byte, EofFormatDiff is chosen to diff from previously rejected contract according to EIP3541
    public static ReadOnlySpan<byte> MAGIC => [0xEF, 0x00];
    public const byte ONE_BYTE_LENGTH = 1;
    public const byte TWO_BYTE_LENGTH = 2;
    public const byte VERSION_OFFSET = TWO_BYTE_LENGTH; // magic length

    private static readonly Dictionary<byte, IEofVersionHandler> _eofVersionHandlers = [];
    internal static ILogger Logger { get; set; } = NullLogger.Instance;

    static EofValidator()
    {
        _eofVersionHandlers.Add(Eof1.VERSION, new Eof1());
    }

    /// <summary>
    /// returns whether the code passed is supposed to be treated as Eof regardless of its validity.
    /// </summary>
    /// <param name="container">Machine code to be checked</param>
    /// <returns></returns>
    public static bool IsEof(ReadOnlyMemory<byte> container, [NotNullWhen(true)] out byte version)
        => IsEof(container.Span, out version);

    /// <summary>
    /// returns whether the code passed is supposed to be treated as Eof regardless of its validity.
    /// </summary>
    /// <param name="container">Machine code to be checked</param>
    /// <returns></returns>
    public static bool IsEof(ReadOnlySpan<byte> container, [NotNullWhen(true)] out byte version)
    {
        if (container.Length > MAGIC.Length)
        {
            version = container[MAGIC.Length];
            return container.StartsWith(MAGIC);
        }
        else
        {
            version = 0;
            return false;
        }
    }

    public static bool IsValidEofHeader(ReadOnlyMemory<byte> code, [NotNullWhen(true)] out EofHeader? header)
    {
        if (IsEof(code, out byte version) && _eofVersionHandlers.TryGetValue(version, out IEofVersionHandler handler))
        {
            return handler.TryParseEofHeader(code, ValidationStrategy.Validate, out header);
        }

        if (Logger.IsTrace) Logger.Trace($"EOF: Eof not recognized");
        header = null;
        return false;
    }

    public static bool IsValidEof(ReadOnlyMemory<byte> code, ValidationStrategy strategy, [NotNullWhen(true)] out EofContainer? eofContainer)
    {
        if (strategy == ValidationStrategy.None)
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: No validation");
            eofContainer = null;
            return true;
        }

        if (strategy.HasFlag(ValidationStrategy.HasEofMagic) && !code.Span.StartsWith(MAGIC))
        {
            if (Logger.IsTrace) Logger.Trace($"EOF: No MAGIC as start of code");
            eofContainer = null;
            return false;
        }

        if (IsEof(code, out byte version) && _eofVersionHandlers.TryGetValue(version, out IEofVersionHandler handler))
        {
            return handler.TryGetEofContainer(strategy, out eofContainer, code);
        }

        if (Logger.IsTrace) Logger.Trace($"EOF: Not EOF");
        eofContainer = null;
        return false;
    }
}
