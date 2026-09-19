// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.BeaconChain.Spec;

/// <summary>Thrown when the execution layer's chain id has no known beacon chain spec.</summary>
/// <remarks>
/// The beacon network is derived from <c>ISpecProvider.ChainId</c> rather than a separate config
/// knob, so the execution and consensus layers can never disagree about which chain they follow.
/// An unrecognized chain id must fail startup rather than silently default to mainnet.
/// </remarks>
public sealed class UnsupportedBeaconNetworkException(ulong chainId)
    : Exception($"The embedded beacon chain driver does not support chain id {chainId}. " +
                "Supported chain ids: mainnet (1), hoodi (560048).")
{
    public ulong ChainId { get; } = chainId;
}
