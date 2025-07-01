// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Data;

/// <summary>
/// Response for eth_sendBundle containing the bundle hash
/// </summary>
public readonly struct BundleResponseForRpc
{
    public Hash256 BundleHash { get; init; }

    public BundleResponseForRpc(Hash256 bundleHash)
    {
        BundleHash = bundleHash;
    }
}