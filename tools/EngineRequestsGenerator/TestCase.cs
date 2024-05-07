// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace EngineRequestsGenerator;

public enum TestCase
{
    [TestCaseMetadata("Warmup", "warmup")]
    Warmup,

    [TestCaseMetadata("Simple transfers", "ETH transfers")]
    Transfers,

    [TestCaseMetadata("TxDataZero", "Data with zeros")]
    TxDataZero,

    [TestCaseMetadata("Keccak", "keccak calculations")]
    Keccak256From1Byte,

    [TestCaseMetadata("Keccak", "keccak calculations")]
    Keccak256From8Bytes,

    [TestCaseMetadata("Keccak", "keccak calculations")]
    Keccak256From32Bytes,

    [TestCaseMetadata("SHA2From32Bytes", "SHA-2 calculations")]
    SHA2From32Bytes
}
