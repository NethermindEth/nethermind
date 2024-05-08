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

    [TestCaseMetadata("Push0Pop", "pushing zeros to stack, then popping it")]
    Push0Pop,

    [TestCaseMetadata("Caller", "pushing caller address to stack")]
    Caller,

    [TestCaseMetadata("CallerPop", "pushing caller address to stack, then popping it")]
    CallerPop,

    // [TestCaseMetadata("BalanceNonExisting", "checking balances of non existing accounts")]
    // BalanceNonExisting,

    [TestCaseMetadata("SHA256From1Byte", "SHA-2 calculations from 1 byte")]
    SHA2From1Byte,

    [TestCaseMetadata("SHA256From8Bytes", "SHA-2 calculations from 8 bytes")]
    SHA2From8Bytes,

    [TestCaseMetadata("SHA256From32Bytes", "SHA-2 calculations from 32 bytes")]
    SHA2From32Bytes,

    [TestCaseMetadata("SHA256From128Bytes", "SHA-2 calculations from 128 bytes")]
    SHA2From128Bytes
}
