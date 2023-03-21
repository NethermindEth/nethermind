// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Merge.Plugin.Data
{
    /// <summary>
    /// Result of engine_newPayloadV1 call.
    ///
    /// <seealso cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#PayloadStatusV1"/>
    /// </summary>
    public class PayloadStatusV1
    {
        public static readonly PayloadStatusV1 Syncing = new() { Status = PayloadStatus.Syncing };

        public static readonly PayloadStatusV1 Accepted = new() { Status = PayloadStatus.Accepted };

        public static PayloadStatusV1 Invalid(Keccak? latestValidHash) => new()
        {
            Status = PayloadStatus.Invalid,
            LatestValidHash = latestValidHash
        };

        /// <summary>
        /// One of <see cref="PayloadStatus"/>.
        /// </summary>
        public string Status { get; set; } = PayloadStatus.Invalid;

        /// <summary>
        /// Hash of the most recent valid block in the branch defined by payload and its ancestors.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public Keccak? LatestValidHash { get; set; }

        /// <summary>
        /// Message providing additional details on the validation error if the payload is classified as <see cref="PayloadStatus.Invalid"/>.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string? ValidationError { get; set; }
    }
}
