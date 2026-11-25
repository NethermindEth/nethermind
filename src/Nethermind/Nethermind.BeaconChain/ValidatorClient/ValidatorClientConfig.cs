// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.ValidatorClient;

/// <inheritdoc cref="IValidatorClientConfig"/>
public class ValidatorClientConfig : IValidatorClientConfig
{
    public bool Enabled { get; set; }
    public string? RemoteSignerUrl { get; set; }
    public string[] PublicKeys { get; set; } = [];
    public string? FeeRecipient { get; set; }
}
