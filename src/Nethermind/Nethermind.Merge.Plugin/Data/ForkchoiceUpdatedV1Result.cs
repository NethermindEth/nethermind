// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.Data
{
    /// <summary>
    /// Result of engine_forkChoiceUpdate call.
    ///
    /// <seealso cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#response-1"/>
    /// </summary>
    public class ForkchoiceUpdatedV1Result
    {
        public static readonly ResultWrapper<ForkchoiceUpdatedV1Result> Syncing = ResultWrapper<ForkchoiceUpdatedV1Result>.Success(new ForkchoiceUpdatedV1Result { PayloadId = null, PayloadStatus = PayloadStatusV1.Syncing });

        public static ResultWrapper<ForkchoiceUpdatedV1Result> Valid(string? payloadId, Keccak? latestValidHash) =>
            ResultWrapper<ForkchoiceUpdatedV1Result>.Success(
                new ForkchoiceUpdatedV1Result
                {
                    PayloadId = payloadId,
                    PayloadStatus = new PayloadStatusV1
                    {
                        Status = Data.PayloadStatus.Valid,
                        LatestValidHash = latestValidHash
                    }
                });

        public static ResultWrapper<ForkchoiceUpdatedV1Result> Invalid(Keccak? latestValidHash, string? validationError = null) =>
            ResultWrapper<ForkchoiceUpdatedV1Result>.Success(
                new ForkchoiceUpdatedV1Result
                {
                    PayloadStatus = new PayloadStatusV1
                    {
                        Status = Data.PayloadStatus.Invalid,
                        LatestValidHash = latestValidHash,
                        ValidationError = validationError
                    }
                });

        public static ResultWrapper<ForkchoiceUpdatedV1Result> Error(string message, int errorCode) => ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(message, errorCode);

        /// <summary>
        /// Status
        /// </summary>
        public PayloadStatusV1 PayloadStatus { get; set; } = PayloadStatusV1.Syncing;

        /// <summary>
        /// Identifier of the payload build process or null if there is none.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string? PayloadId { get; set; }

        public static implicit operator Task<ForkchoiceUpdatedV1Result>(ForkchoiceUpdatedV1Result result) => Task.FromResult(result);
    }
}
