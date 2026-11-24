// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Ethereum.Test.Base
{
    public class TestBlockJson
    {
        public TestBlockHeaderJson? BlockHeader { get; set; }
        public TestBlockHeaderJson[]? UncleHeaders { get; set; }
        public string? Rlp { get; set; }
        public string? DependencyProof { get; set; }
        public BlockAccessListAccountJson[]? BlockAccessList { get; set; }

        /// <summary>An expected-invalid block has no top-level fields; its access list and proof are under <c>rlp_decoded</c>.</summary>
        [JsonPropertyName("rlp_decoded")]
        public TestBlockRlpDecodedJson? RlpDecoded { get; set; }

        public LegacyTransactionJson[]? Transactions { get; set; }
        public string? ExpectException { get; set; }

        public ExecutionWitnessJson? ExecutionWitness { get; set; }
        public string? StatelessInputBytes { get; set; }
        public string? StatelessOutputBytes { get; set; }
        public bool? ExecutionWitnessMutated { get; set; }

        /// <summary>The EIP-7928 access list delivered with the block, valid or not.</summary>
        [JsonIgnore]
        public BlockAccessListAccountJson[]? DeliveredBlockAccessList => BlockAccessList ?? RlpDecoded?.BlockAccessList;

        /// <summary>The EIP-8288 dependency proof delivered beside the block, valid or not.</summary>
        [JsonIgnore]
        public string? DeliveredDependencyProof => DependencyProof ?? RlpDecoded?.DependencyProof;
    }

    public class TestBlockRlpDecodedJson
    {
        public BlockAccessListAccountJson[]? BlockAccessList { get; set; }
        public string? DependencyProof { get; set; }
    }
}
