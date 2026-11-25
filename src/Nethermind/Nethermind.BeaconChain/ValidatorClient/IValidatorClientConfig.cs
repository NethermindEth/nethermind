// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.BeaconChain.ValidatorClient;

/// <summary>
/// Configuration for the validator duties the embedded beacon chain driver performs. Deliberately separate from
/// <see cref="IBeaconChainConfig"/>: the duties are a consumer of the node, not a knob of the sync driver.
/// </summary>
public interface IValidatorClientConfig : IConfig
{
    [ConfigItem(Description = "Whether to perform the attester, sync committee and block proposal duties of the validators of `PublicKeys`, signing through the remote signer at `RemoteSignerUrl`. Requires BeaconChain.Enabled.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The base URL of the Web3Signer-compatible remote signer that holds the validator keys and enforces slashing protection, for example http://127.0.0.1:9000. Required when `Enabled` is set.", DefaultValue = "null")]
    string? RemoteSignerUrl { get; set; }

    [ConfigItem(Description = "The BLS public keys, as 0x-prefixed hex, of the validators to perform duties for. The remote signer must hold each key. At least one is required when `Enabled` is set.", DefaultValue = "[]")]
    string[] PublicKeys { get; set; }

    [ConfigItem(Description = "The execution address, as 0x-prefixed hex, that the blocks the validators of `PublicKeys` propose pay their fees to. Required when `Enabled` is set: the validator client does not start without it.", DefaultValue = "null")]
    string? FeeRecipient { get; set; }
}
