// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Serialization.Json;

namespace Nethermind.Evm;

/// <summary>
/// Reads <see cref="AccountOverride.Code"/> as <see cref="ByteArrayConverter"/> does, but returns one shared array
/// for code text that requests sent before.
/// </summary>
/// <remarks>
/// Text is decoded by <see cref="ByteArrayConverter"/> whenever it is not interned yet, so an invalid one fails exactly
/// as before. Text the reader does not hold as one unescaped span is always decoded.
/// </remarks>
public sealed class OverrideCodeJsonConverter : ByteArrayConverter
{
    private static readonly bool InternerDisabled = Environment.GetEnvironmentVariable("NETHERMIND_DIAGNOSTIC_DISABLE_OVERRIDE_CODE_INTERNER") == "1";
    private readonly OverrideCodeInterner? _interner;

    static OverrideCodeJsonConverter() => Console.Error.WriteLine(
        $"RPC override diagnostic: variant={(InternerDisabled ? "V1" : "H")}; interner={(InternerDisabled ? "disabled" : "enabled")}; code-info-cache=4-way");

    public OverrideCodeJsonConverter() : this(InternerDisabled ? null : OverrideCodeInterner.Shared) { }

    internal OverrideCodeJsonConverter(OverrideCodeInterner? interner) => _interner = interner;

    public override byte[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (_interner is null || reader.TokenType != JsonTokenType.String || reader.HasValueSequence || reader.ValueIsEscaped)
            return base.Read(ref reader, typeToConvert, options);

        ReadOnlySpan<byte> text = reader.ValueSpan;
        if (!OverrideCodeInterner.Accepts(text))
            return base.Read(ref reader, typeToConvert, options);

        int fastHash = OverrideCodeInterner.HashOf(text);
        byte[]? code = _interner.Find(text, fastHash);
        if (code is null)
        {
            code = base.Read(ref reader, typeToConvert, options);
            if (code is not null) _interner.Add(text, fastHash, code);
        }

        return code;
    }
}
