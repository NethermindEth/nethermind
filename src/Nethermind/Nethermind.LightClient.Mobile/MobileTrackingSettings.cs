// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;

namespace Nethermind.LightClient.Mobile;

/// <summary>The mainnet account and optional ERC-20 balances to follow alongside its native ETH balance.</summary>
public sealed record MobileTrackingSettings(string AccountAddress, bool Usdt = true, bool Dai = true, bool Usdc = true)
{
    /// <summary>Validates and normalizes an account, checking EIP-55 when mixed-case hex is supplied.</summary>
    public static bool TryCreate(string? accountAddress, bool usdt, bool dai, bool usdc,
        [NotNullWhen(true)] out MobileTrackingSettings? settings)
    {
        settings = null;
        string? account = accountAddress?.Trim();
        if (account is not { Length: 42 } || !account.StartsWith("0x", StringComparison.Ordinal)
            || !Address.IsValidAddress(account, allowPrefix: true)) return false;

        string normalized = new Address(account).ToString(withEip55Checksum: true);
        bool lower = false, upper = false;
        foreach (char digit in account.AsSpan(2))
        {
            lower |= digit is >= 'a' and <= 'f';
            upper |= digit is >= 'A' and <= 'F';
        }
        if (lower && upper && !StringComparer.Ordinal.Equals(account, normalized)) return false;
        settings = new(normalized, usdt, dai, usdc);
        return true;
    }

    internal MobileToken[] SelectedTokens()
    {
        List<MobileToken> tokens = new(3);
        if (Usdt) tokens.Add(MobileToken.Usdt);
        if (Dai) tokens.Add(MobileToken.Dai);
        if (Usdc) tokens.Add(MobileToken.Usdc);
        return tokens.ToArray();
    }
}

internal sealed record MobileToken(string Symbol, string ContractAddress, int Decimals)
{
    internal static readonly MobileToken Usdt = new("USDT", "0xdAC17F958D2ee523a2206206994597C13D831ec7", 6);
    internal static readonly MobileToken Dai = new("DAI", "0x6B175474E89094C44Da98b954EedeAC495271d0F", 18);
    internal static readonly MobileToken Usdc = new("USDC", "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48", 6);
}
