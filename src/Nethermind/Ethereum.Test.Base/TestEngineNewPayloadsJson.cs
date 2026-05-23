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

        /// <summary>
        /// Optional execution witness expected for this payload.
        /// Present in <c>blockchain_test_engine</c> fixtures from the zkevm archive.
        /// Contains <c>state</c>, <c>codes</c>, and <c>headers</c> byte lists.
        /// </summary>
        public JsonElement? ExecutionWitness { get; set; }

        /// <summary>
        /// When <c>true</c>, the payload's <c>executionWitness</c> was deliberately corrupted
        /// for stateless-validator negative testing. Stateful nodes like Nethermind must skip
        /// witness comparison for these payloads.
        /// </summary>
        public bool ExecutionWitnessMutated { get; set; }

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
