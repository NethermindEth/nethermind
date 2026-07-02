// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Test.Base
{
    /// <summary>
    /// EELS-produced reference execution witness as published in EEST zkEVM fixtures.
    /// All three sections are hex-encoded byte arrays; treat as sets when comparing
    /// against a client-produced <see cref="Nethermind.Consensus.Stateless.Witness"/>.
    /// </summary>
    public class ExecutionWitnessJson
    {
        public string[]? State { get; set; }
        public string[]? Codes { get; set; }
        public string[]? Headers { get; set; }
    }
}
