// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;

namespace Nethermind.Flashbots.Data;

public class BuilderBlockValidationRequest
{
    public BuilderBlockValidationRequest(Hash256 parentBeaconBlockRoot, long registerGasLimit, SubmitBlockRequest blockRequest)
    {
        ParentBeaconBlockRoot = parentBeaconBlockRoot;
        RegisterGasLimit = registerGasLimit;
        BlockRequest = blockRequest;
    }

    /// <summary>
    /// The block hash of the parent beacon block.
    /// <see cref=https://github.com/flashbots/builder/blob/df9c765067d57ab4b2d0ad39dbb156cbe4965778/eth/block-validation/api.go#L198"/>
    /// </summary>
    [JsonRequired]
    public Hash256 ParentBeaconBlockRoot { get; set; }

    [JsonRequired]
    public long RegisterGasLimit { get; set; }

    [JsonRequired]
    public SubmitBlockRequest BlockRequest { get; set; }
}
