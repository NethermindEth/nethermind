// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Newtonsoft.Json;

namespace Nethermind.Flashbots.Data;

public class BuilderBlockValidationRequest
{

    [JsonProperty("withdrawals_root")]
    [JsonRequired]
    public required Hash256 WithdrawalsRoot { get; set; }

    [JsonProperty("execution_payload")]
    [JsonRequired]
    public required ExecutionPayloadV3 ExecutionPayload { get; set; }

    [JsonProperty("blobs_bundle")]
    [JsonRequired]
    public required BlobsBundleV1 BlobsBundle { get; set; }

    [JsonProperty("mesage")]
    [JsonRequired]
    public required BidTrace Message { get; set; }

    [JsonRequired]
    [JsonProperty("parent_beacon_block_root")]
    public required Hash256 ParentBeaconBlockRoot { get; set; }

    [JsonProperty("register_gas_limit")]
    [System.Text.Json.Serialization.JsonRequired]
    public long RegisterGasLimit { get; set; }

    [JsonProperty("signature")]
    [JsonRequired]
    public required Hash256 Signature { get; set; }

    [JsonProperty("withdrawals")]
    [JsonRequired]
    public required List<Withdrawal> Withdrawals { get; set; }
}


