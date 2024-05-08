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

    [TestCaseMetadata("Keccak", "keccak calculations based on 1 byte source")]
    Keccak256From1Byte,

    [TestCaseMetadata("Keccak", "keccak calculations based on 8 byte source")]
    Keccak256From8Bytes,

    [TestCaseMetadata("Keccak", "keccak calculations based on 32 byte source")]
    Keccak256From32Bytes,

    [TestCaseMetadata("Push0", "pushing zeros to stack")]
    Push0,

    [TestCaseMetadata("Push0Pop", "pushing zeros to stack, then poping it")]
    Push0Pop,

    [TestCaseMetadata("SHA2From32Bytes", "SHA-2 calculations")]
    SHA2From32Bytes
}
