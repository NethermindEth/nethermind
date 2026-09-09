// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc
{
    public class Error
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("data")]
        public object? Data { get; set; }

        [JsonIgnore]
        public bool SuppressWarning { get; set; }

        /// <summary>
        /// Set when the error reports a condition of *this node* rather than a fault in the request, even though it
        /// carries one of the JSON-RPC pre-defined request codes. A disabled namespace is the motivating case: the
        /// caller asked for a legitimate method and the message is a remediation instruction for the operator, so it
        /// must keep its WARN even though the code is <see cref="ErrorCodes.InvalidRequest"/>.
        /// </summary>
        [JsonIgnore]
        public bool OperatorActionable { get; set; }
    }
}
