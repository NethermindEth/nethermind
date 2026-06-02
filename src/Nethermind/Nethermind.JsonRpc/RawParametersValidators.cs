// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc;

internal delegate void RawParameterValidator(
    bool useUtf8Parameters,
    ReadOnlyMemory<byte> providedParametersUtf8,
    JsonElement providedParameters);

internal static class RawParametersValidators
{
    private const string MissingHexPrefixError = "hex string without 0x prefix";
    private const string LeadingZeroHexNumberError = "hex number with leading zero digits";

    internal static RawParameterValidator? Resolve(RawParametersValidation validation) =>
        validation switch
        {
            RawParametersValidation.None => null,
            RawParametersValidation.EthGetBalance => ValidateEthGetBalance,
            _ => throw new ArgumentOutOfRangeException(nameof(validation), validation, null)
        };

    private static void ValidateEthGetBalance(
        bool useUtf8Parameters,
        ReadOnlyMemory<byte> providedParametersUtf8,
        JsonElement providedParameters)
    {
        if (useUtf8Parameters)
        {
            ValidateEthGetBalanceUtf8Parameters(providedParametersUtf8);
            return;
        }

        ValidateEthGetBalanceParameters(providedParameters);
    }

    private static void ValidateEthGetBalanceUtf8Parameters(ReadOnlyMemory<byte> providedParametersUtf8)
    {
        int offset = 0;
        JsonReaderState readerState = default;
        bool started = false;

        if (!JsonRpcArrayReader.TryReadNextItem(providedParametersUtf8, ref offset, ref readerState, ref started, out ReadOnlyMemory<byte> addressParameter))
        {
            return;
        }

        ValidateEthGetBalanceAddress(addressParameter);
        if (!JsonRpcArrayReader.TryReadNextItem(providedParametersUtf8, ref offset, ref readerState, ref started, out ReadOnlyMemory<byte> blockParameter))
        {
            return;
        }

        ValidateEthGetBalanceBlockParameter(blockParameter);
    }

    private static void ValidateEthGetBalanceParameters(JsonElement providedParameters)
    {
        if (providedParameters.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        JsonElement.ArrayEnumerator enumerator = providedParameters.EnumerateArray();
        if (!enumerator.MoveNext())
        {
            return;
        }

        ValidateEthGetBalanceAddress(enumerator.Current);
        if (!enumerator.MoveNext())
        {
            return;
        }

        ValidateEthGetBalanceBlockParameter(enumerator.Current);
    }

    private static void ValidateEthGetBalanceAddress(JsonElement addressParameter)
    {
        if (addressParameter.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string? value = addressParameter.GetString();
        if (string.IsNullOrEmpty(value) || value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new SafePublicMessageFormatException(MissingHexPrefixError);
    }

    private static void ValidateEthGetBalanceAddress(ReadOnlyMemory<byte> addressParameter)
    {
        Utf8JsonReader reader = new(addressParameter.Span, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.String)
        {
            return;
        }

        int maxLength = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        byte[]? rented = null;
        Span<byte> buffer = maxLength <= 128 ? stackalloc byte[128] : (rented = ArrayPool<byte>.Shared.Rent(maxLength));
        try
        {
            ReadOnlySpan<byte> value = buffer[..reader.CopyString(buffer)];
            if (value.IsEmpty || Has0xPrefix(value))
            {
                return;
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        throw new SafePublicMessageFormatException(MissingHexPrefixError);
    }

    private static void ValidateEthGetBalanceBlockParameter(JsonElement blockParameter)
    {
        if (blockParameter.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string? value = blockParameter.GetString();
        if (string.IsNullOrEmpty(value) || IsNamedBlockParameter(value) || !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ReadOnlySpan<char> hexValue = value.AsSpan(2);
        if (hexValue.Length == 0)
        {
            throw new SafePublicMessageFormatException($"hex string \"{Bytes.EmptyHexValue}\"");
        }

        if (hexValue.Length == 64)
        {
            return;
        }

        if (HasLeadingZeroHexQuantity(hexValue))
        {
            throw new SafePublicMessageFormatException(LeadingZeroHexNumberError);
        }
    }

    private static void ValidateEthGetBalanceBlockParameter(ReadOnlyMemory<byte> blockParameter)
    {
        Utf8JsonReader reader = new(blockParameter.Span, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.String)
        {
            return;
        }

        int maxLength = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        byte[]? rented = null;
        Span<byte> buffer = maxLength <= 128 ? stackalloc byte[128] : (rented = ArrayPool<byte>.Shared.Rent(maxLength));
        try
        {
            ReadOnlySpan<byte> value = buffer[..reader.CopyString(buffer)];
            if (value.IsEmpty || IsNamedBlockParameter(value) || !Has0xPrefix(value))
            {
                return;
            }

            ReadOnlySpan<byte> hexValue = value[2..];
            if (hexValue.Length == 0)
            {
                throw new SafePublicMessageFormatException($"hex string \"{Bytes.EmptyHexValue}\"");
            }

            if (hexValue.Length == 64)
            {
                return;
            }

            if (HasLeadingZeroHexQuantity(hexValue))
            {
                throw new SafePublicMessageFormatException(LeadingZeroHexNumberError);
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static bool IsNamedBlockParameter(string value) =>
        value.Length switch
        {
            4 => value.Equals("safe", StringComparison.OrdinalIgnoreCase),
            6 => value.Equals("latest", StringComparison.OrdinalIgnoreCase),
            7 => value.Equals("pending", StringComparison.OrdinalIgnoreCase),
            8 => value.Equals("earliest", StringComparison.OrdinalIgnoreCase),
            9 => value.Equals("finalized", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static bool IsNamedBlockParameter(ReadOnlySpan<byte> value) =>
        value.Length switch
        {
            4 => Ascii.EqualsIgnoreCase(value, "safe"u8),
            6 => Ascii.EqualsIgnoreCase(value, "latest"u8),
            7 => Ascii.EqualsIgnoreCase(value, "pending"u8),
            8 => Ascii.EqualsIgnoreCase(value, "earliest"u8),
            9 => Ascii.EqualsIgnoreCase(value, "finalized"u8),
            _ => false,
        };

    private static bool Has0xPrefix(ReadOnlySpan<byte> value) =>
        value.Length >= 2 && Ascii.EqualsIgnoreCase(value[..2], "0x"u8);

    private static bool HasLeadingZeroHexQuantity(ReadOnlySpan<char> hexValue) =>
        hexValue.Length > 1 && hexValue[0] == '0';

    private static bool HasLeadingZeroHexQuantity(ReadOnlySpan<byte> hexValue) =>
        hexValue.Length > 1 && hexValue[0] == (byte)'0';
}
