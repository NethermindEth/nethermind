// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.JsonConverters;

/// <summary>
/// Shared flag controlling EIP-1474 QUANTITY hex validation (no leading zero digits).
/// Set by <c>EthereumJsonSerializer.StrictHexFormat</c> and read by QUANTITY-typed JSON converters.
/// </summary>
public static class JsonRpcQuantityFormat
{
    /// <summary>
    /// When <see langword="true"/>, hex QUANTITY strings with leading zero digits are rejected.
    /// Mirrors <c>EthereumJsonSerializer.StrictHexFormat</c>.
    /// </summary>
    public static volatile bool StrictMode = false;
}
