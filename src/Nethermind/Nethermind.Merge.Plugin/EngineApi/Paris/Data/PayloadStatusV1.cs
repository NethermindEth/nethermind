// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Newtonsoft.Json;

namespace Nethermind.Merge.Plugin.EngineApi.Paris.Data
{
    public interface IPayloadStatus<out T>
    {
        static abstract T Syncing { get; }
        static abstract T InvalidBlockHash { get; }
        static abstract T Accepted { get; }
        static abstract T Invalid(Keccak? latestValidHash, string? validationError = null);
        static abstract T Valid(Keccak? latestValidHash);
    }

    /// <summary>
    /// Result of engine_newPayloadV1 call.
    ///
    /// <seealso href="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#PayloadStatusV1"/>
    /// </summary>
    public class PayloadStatusV1 : IPayloadStatus<PayloadStatusV1>
    {
        public static PayloadStatusV1 Invalid(Keccak? latestValidHash, string? validationError = null) =>
            new() { Status = PayloadStatus.Invalid, LatestValidHash = latestValidHash, ValidationError = validationError };
        public static PayloadStatusV1 Valid(Keccak? latestValidHash) => new() { Status = PayloadStatus.Valid, LatestValidHash = latestValidHash };

        public static PayloadStatusV1 InvalidBlockHash { get; } = new() { Status = PayloadStatus.InvalidBlockHash };

        public static PayloadStatusV1 Syncing { get; } = new() { Status = PayloadStatus.Syncing };

        public static PayloadStatusV1 Accepted { get; } = new() { Status = PayloadStatus.Accepted };

        /// <summary>
        /// One of <see cref="PayloadStatus"/>.
        /// </summary>
        public string Status { get; set; } = PayloadStatus.Invalid;

        /// <summary>
        /// Hash of the most recent valid block in the branch defined by payload and its ancestors.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Keccak? LatestValidHash { get; set; }

        /// <summary>
        /// Message providing additional details on the validation error if the payload is classified as <see cref="PayloadStatus.Invalid"/> or <see cref="PayloadStatus.InvalidBlockHash"/>.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public string? ValidationError { get; set; }
    }
}
