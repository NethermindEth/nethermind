// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.LightClient.Rpc;

namespace Nethermind.LightClient.Mobile;

internal sealed class MobileTokenReader(VerifiedCall calls, Address account, TimeProvider clock)
{
    internal async Task<MobileTokenBalance> ReadAsync(MobileToken token, VerifiedHead head, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        JsonElement request = JsonSerializer.SerializeToElement(new
        {
            to = token.ContractAddress,
            data = "0x70a08231" + account.ToString(withZeroX: false, withEip55Checksum: false).PadLeft(64, '0'),
            gas = "0x7a120",
        });
        string result = await calls.ExecuteAsync(head, request, estimateGas: false, cancellationToken);
        if (result.Length != 66 || !result.StartsWith("0x", StringComparison.Ordinal))
            throw new InvalidDataException($"{token.Symbol} balanceOf returned an invalid uint256.");

        BigInteger units = new(Convert.FromHexString(result.AsSpan(2)), isUnsigned: true, isBigEndian: true);
        return new(token.Symbol, token.Decimals, units.ToString(CultureInfo.InvariantCulture), head.Number,
            head.BlockHash.ToString(), head.StateRoot.ToString(), clock.GetUtcNow(), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
