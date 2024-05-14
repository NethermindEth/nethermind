// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace EngineRequestsGenerator;

public enum TestCase
{
    [TestCaseMetadata("Warmup", "warmup")]
    Warmup,

    [TestCaseMetadata("ETH transfers", "simple ETH transfers")]
    Transfers,

    [TestCaseMetadata("Tx with big zero data", "single transaction with large extra full of zeros")]
    TxDataZero,

    [TestCaseMetadata("Keccak256 from 1 byte", "keccak calculations based on 1-byte source data")]
    Keccak256From1Byte,

    [TestCaseMetadata("Keccak256 from 8 bytes", "keccak calculations based on 8-byte source data")]
    Keccak256From8Bytes,

    [TestCaseMetadata("Keccak256 from 32 bytes", "keccak calculations based on 32-byte source data")]
    Keccak256From32Bytes,

    [TestCaseMetadata("Push0", "endlessly pushing zeros to stack (1000 per 1 contract)")]
    Push0,

    [TestCaseMetadata("Push0 - Pop", "endlessly pushing zeros to stack, then popping it")]
    Push0Pop,

    [TestCaseMetadata("MStore - zero", "endlessly pushing zero value to memory with offset zero")]
    MStoreZero,

    [TestCaseMetadata("MStore - random", "endlessly pushing random value to memory with offset zero")]
    MStoreRandom,

    [TestCaseMetadata("Caller", "endlessly pushing caller address to stack (1000 per 1 contract)")]
    Caller,

    [TestCaseMetadata("Caller - Pop", "endlessly pushing caller address to stack, then popping it")]
    CallerPop,

    [TestCaseMetadata("Address", "endlessly pushing account address to stack (1000 per 1 contract)")]
    Address,

    [TestCaseMetadata("Origin", "endlessly pushing execution origination address to stack (1000 per 1 contract)")]
    Origin,

    [TestCaseMetadata("BaseFee", "endlessly pushing current base fee to stack (1000 per 1 contract)")]
    BaseFee,

    [TestCaseMetadata("BlobBaseFee", "endlessly pushing current blob base fee to stack (1000 per 1 contract)")]
    BlobBaseFee,

    [TestCaseMetadata("BlobHash", "endlessly pushing zero as index and BlobHash opcode to stack when there were no blobs (1000 per 1 contract)")]
    BlobHashZero,

    // [TestCaseMetadata("BalanceNonExisting", "checking balances of non existing accounts")]
    // BalanceNonExisting,

    [TestCaseMetadata("SHA-2 precompile from 1 byte", "SHA-2 precompile calculations based on 1-byte source data")]
    SHA2From1Byte,

    [TestCaseMetadata("SHA-2 precompile from 8 bytes", "SHA-2 precompile calculations based on 8-byte source data")]
    SHA2From8Bytes,

    [TestCaseMetadata("SHA-2 precompile from 32 bytes", "SHA-2 precompile calculations based on 32-byte source data")]
    SHA2From32Bytes,

    [TestCaseMetadata("SHA-2 precompile from 128 bytes", "SHA-2 precompile calculations based on 128-byte source data")]
    SHA2From128Bytes,

    [TestCaseMetadata("SStore - one storage key, repeating zero value", "SStore - repeating storing zero in single storage key of single account")]
    SStoreOneAccountOneKeyZeroValue,

    [TestCaseMetadata("SStore - one storage key, repeating constant value", "SStore - repeating storing the same 32-byte word in single storage key of single account")]
    SStoreOneAccountOneKeyConstantValue,

    [TestCaseMetadata("SStore - one storage key, repeating random values", "SStore - repeating storing random 32-byte values in single storage key of single account")]
    SStoreOneAccountOneKeyRandomValue,

    [TestCaseMetadata("SStore - one storage key, repeating two values, zero and non-zero", "SStore - repeating storing zero and then storing 32-byte word in single storage key of single account")]
    SStoreOneAccountOneKeyTwoValues,

    [TestCaseMetadata("SStore - many accounts, consecutive storage keys, random values", "SStore - storing random 32-byte values in consecutive storage keys of many accounts")]
    SStoreManyAccountsConsecutiveKeysRandomValue,

    [TestCaseMetadata("SStore - many accounts, random storage keys, random values", "SStore - storing random 32-byte values in random storage keys of many accounts")]
    SStoreManyAccountsRandomKeysRandomValue,

    [TestCaseMetadata("SStore - many accounts, consecutive storage keys, zero values", "SStore - storing zeros in consecutive storage keys of many accounts")]
    SStoreManyAccountsConsecutiveKeysZeroValue,

    [TestCaseMetadata("SStore - many accounts, random storage keys, zero values", "SStore - storing zeros in random storage keys of many accounts")]
    SStoreManyAccountsRandomKeysZeroValue,
}
