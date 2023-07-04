// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Newtonsoft.Json;

namespace Nethermind.State.Test.Runner
{
    public class StateTestTxTraceState
    {
        [JsonProperty("stateRoot")]
        public Keccak StateRoot { get; set; }
    }
}
