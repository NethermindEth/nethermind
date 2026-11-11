// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;

namespace Ethereum.Test.Base
{
    public class EthereumTestResult
    {
        public EthereumTestResult(string? name, string? fork, bool pass)
        {
            Name = name ?? "unnamed";
            Fork = fork ?? "unknown";
            Pass = pass;
        }

        public EthereumTestResult(string? name, string? fork, string loadFailure)
        {
            Name = name ?? "unnamed";
            Fork = fork ?? "unknown";
            Pass = false;
            LoadFailure = loadFailure;
            Error = loadFailure;
        }

        public EthereumTestResult(string? name, string? loadFailure)
            : this(name, null, loadFailure)
        {
        }

        [JsonIgnore]
        public string? LoadFailure { get; set; }
        public string Name { get; set; }
        public bool Pass { get; set; }
        public string Fork { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Error { get; set; }

        [JsonIgnore]
        public double TimeInMs { get; set; }

        /// <summary>
        /// Post-execution state root. Only populated by state tests; blockchain/engine and
        /// transaction results leave it null, so the field is omitted from their JSON.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Hash256? StateRoot { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Hash256? LastBlockHash { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastPayloadStatus { get; set; }

        /// <summary>
        /// Every block or payload the client rejected, in fixture order, with the client's own error. Only
        /// blockchain/engine results carry it; the runner reports the reason and leaves checking it to the consumer.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<BlockRejection>? Rejections { get; set; }
    }

    /// <param name="Index">The block's position in the fixture's <c>blocks</c>, or the payload's in <c>engineNewPayloads</c>.</param>
    /// <param name="Hash">The rejected block's hash, when the client computed one.</param>
    /// <param name="Error">The client's error, verbatim.</param>
    public sealed record BlockRejection(
        int Index,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Hash256? Hash,
        string Error);
}
