// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

using Nethermind.Core.Crypto;

namespace Nethermind.State.Test.Runner
{
    public class StateTestTxTraceState
    {
        [JsonPropertyName("stateRoot")]
        public Hash256 StateRoot { get; set; }
    }
}
