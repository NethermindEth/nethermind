// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Ethereum.Test.Base
{
    public class TestEngineNewPayloadsJson
    {
        public JsonElement[] Params { get; set; }
        public string? NewPayloadVersion { get; set; }
        public string? ForkChoiceUpdatedVersion { get; set; }
        public string? ValidationError { get; set; }
        // EIP-7805: expected PayloadStatusV2.inclusionListSatisfied; only meaningful for a VALID payload.
        public bool? InclusionListSatisfied { get; set; }

        /// <summary>
        /// The <c>inclusionListSatisfied</c> the fixture expects on the fork-choice update that follows the
        /// payload, overriding the value inherited from <see cref="InclusionListSatisfied"/>.
        /// </summary>
        /// <remarks>
        /// A JSON <c>null</c> asserts the field is absent, which an omitted property cannot express — hence
        /// <see cref="JsonElement"/> (<c>Undefined</c> when the fixture is silent) rather than <c>bool?</c>.
        /// </remarks>
        public JsonElement ForkchoiceUpdatedInclusionListSatisfied { get; set; }

        public ExecutionWitnessJson? ExecutionWitness { get; set; }
        public bool? ExecutionWitnessMutated { get; set; }

        /// <summary>
        /// The JSON-RPC error code the fixture expects <c>engine_newPayloadV*</c> to answer with
        /// instead of a payload status, or null when the payload is expected to be validated.
        /// </summary>
        public string? ErrorCode { get; set; }

        public class ParamsExecutionPayload
        {
            public string ParentHash { get; set; }
            public string FeeRecipient { get; set; }
            public string StateRoot { get; set; }
            public string ReceiptsRoot { get; set; }
            public string LogsBloom { get; set; }
            public string BlockNumber { get; set; }
            public string GasLimit { get; set; }
            public string GasUsed { get; set; }
            public string Timestamp { get; set; }
            public string ExtraData { get; set; }
            public string PrevRandao { get; set; }
            public string BaseFeePerGas { get; set; }
            public string BlobGasUsed { get; set; }
            public string ExcessBlobGas { get; set; }
            public string BlockHash { get; set; }
            public string[] Transactions { get; set; }
            public JsonElement[]? Withdrawals { get; set; }
            public string? BlockAccessList { get; set; }
            public string? SlotNumber { get; set; }
        }
    }
}
