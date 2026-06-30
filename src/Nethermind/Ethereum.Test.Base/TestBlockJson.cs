// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Test.Base
{
    public class TestBlockJson
    {
        public TestBlockHeaderJson? BlockHeader { get; set; }
        public TestBlockHeaderJson[]? UncleHeaders { get; set; }
        public string? Rlp { get; set; }
        public LegacyTransactionJson[]? Transactions { get; set; }
        public string? ExpectException { get; set; }

        // zkEVM-only fields (present in EEST tests-zkevm fixtures, null elsewhere). ExecutionWitness is the
        // EELS reference witness asserted in BlockchainTestBase; StatelessInput/OutputBytes drive the stateless check.
        public ExecutionWitnessJson? ExecutionWitness { get; set; }
        public string? StatelessInputBytes { get; set; }
        public string? StatelessOutputBytes { get; set; }

        // EIP-8025 mutated-witness marker. The RLP blockchain_tests JSON does not carry it (only
        // blockchain_tests_engine does), so on the RLP path it is stamped at load time by cross-referencing
        // the engine tree, letting witness comparison skip corrupted blocks without hardcoding test names.
        public bool? ExecutionWitnessMutated { get; set; }
    }
}
