// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Ethereum.Test.Base
{
    public class TestEngineNewPayloadsJson
    {
        public JsonElement[] Params { get; set; }
        // public EngineNewPayloadParams Params { get; set; }
        public string? NewPayloadVersion { get; set; }
        public string? ForkChoiceUpdatedVersion { get; set; }

        // public class EngineNewPayloadParams
        // {
        //     public ParamsExecutionPayload ExecutionPayload;
        //     public string[] BlobVersionedHashes;
        //     public string ParentBeaconBlockRoot;
        //     public string ValidationError;
        // }

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
            public string[] Withdrawals { get; set; }
            public string BlockAccessList { get; set; }
        }
    }
}
