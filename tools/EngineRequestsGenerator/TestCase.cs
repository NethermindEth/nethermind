// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace EngineRequestsGenerator;

public enum TestCase
{
    [TestCaseMetadata("Warmup", "Warmup")]
    Warmup,

    [TestCaseMetadata("ETH transfers", "All block gas limit consumed by simple ETH transfers")]
    Transfers,

    [TestCaseMetadata("Tx with big zero data", "Single transaction with large extra data full of zeros")]
    TxDataZero,

    [TestCaseMetadata("Keccak256 from 1 byte", "Keccak calculations based on 1-byte source data")]
    Keccak256From1Byte,

    [TestCaseMetadata("Keccak256 from 8 bytes", "Keccak calculations based on 8-byte source data")]
    Keccak256From8Bytes,

    [TestCaseMetadata("Keccak256 from 32 bytes", "Keccak calculations based on 32-byte source data")]
    Keccak256From32Bytes,

    [TestCaseMetadata("Push0", "Endlessly pushing zeros to stack (1000 per 1 contract)")]
    Push0,

    [TestCaseMetadata("Push0-Pop", "Endlessly pushing zeros to stack, then popping it")]
    Push0Pop,

    [TestCaseMetadata("Gas", "Endlessly pushing amount of remaining gas to stack (1000 per 1 contract)")]
    Gas,

    [TestCaseMetadata("Gas-Pop", "Endlessly pushing amount of remaining gas to stack, then popping it")]
    GasPop,

    [TestCaseMetadata("SelfBalance", "Endlessly pushing self balance to stack (1000 per 1 contract)")]
    SelfBalance,

    [TestCaseMetadata("JumpDest", "Block full of JumpDest opcode only")]
    JumpDest,

    [TestCaseMetadata("MSize", "Endlessly pushing memory size to stack (1000 per 1 contract)")]
    MSize,

    [TestCaseMetadata("MStore - zero", "Endlessly pushing zero value to memory with offset zero")]
    MStoreZero,

    [TestCaseMetadata("MStore - random", "Endlessly pushing random value to memory with offset zero")]
    MStoreRandom,

    [TestCaseMetadata("Caller", "Endlessly pushing caller address to stack (1000 per 1 contract)")]
    Caller,

    [TestCaseMetadata("Caller-Pop", "Endlessly pushing caller address to stack, then popping it")]
    CallerPop,

    [TestCaseMetadata("Address", "Endlessly pushing account address to stack (1000 per 1 contract)")]
    Address,

    [TestCaseMetadata("Origin", "Endlessly pushing execution origination address to stack (1000 per 1 contract)")]
    Origin,

    [TestCaseMetadata("CoinBase", "Endlessly pushing current block's coinbase to stack (1000 per 1 contract)")]
    CoinBase,

    [TestCaseMetadata("Timestamp", "Endlessly pushing current block's timestamp to stack (1000 per 1 contract)")]
    Timestamp,

    [TestCaseMetadata("Number", "Endlessly pushing current block's number to stack (1000 per 1 contract)")]
    Number,

    [TestCaseMetadata("PrevRandao", "Endlessly pushing previous block's randao mix to stack (1000 per 1 contract)")]
    PrevRandao,

    [TestCaseMetadata("GasLimit", "Endlessly pushing current block's gas limit to stack (1000 per 1 contract)")]
    GasLimit,

    [TestCaseMetadata("ChainId", "Endlessly pushing chain ID to stack (1000 per 1 contract)")]
    ChainId,

    [TestCaseMetadata("BaseFee", "Endlessly pushing current base fee to stack (1000 per 1 contract)")]
    BaseFee,

    [TestCaseMetadata("BlobBaseFee", "Endlessly pushing current blob base fee to stack (1000 per 1 contract)")]
    BlobBaseFee,

    [TestCaseMetadata("BlobHash", "Endlessly pushing zero as index and BlobHash opcode to stack when there were no blobs (1000 per 1 contract)")]
    BlobHashZero,

    [TestCaseMetadata("CodeCopy", "Endlessly loading 32-bytes of code to the memory")]
    CodeCopy,

    [TestCaseMetadata("EcRecover precompile", "EcRecover precompile calculations")]
    EcRecover,

    [TestCaseMetadata("SHA-2 precompile from 1 byte", "SHA-2 precompile calculations based on 1-byte source data")]
    SHA2From1Byte,

    [TestCaseMetadata("SHA-2 precompile from 8 bytes", "SHA-2 precompile calculations based on 8-byte source data")]
    SHA2From8Bytes,

    [TestCaseMetadata("SHA-2 precompile from 32 bytes", "SHA-2 precompile calculations based on 32-byte source data")]
    SHA2From32Bytes,

    [TestCaseMetadata("SHA-2 precompile from 128 bytes", "SHA-2 precompile calculations based on 128-byte source data")]
    SHA2From128Bytes,

    [TestCaseMetadata("SHA-2 precompile from 1024 bytes", "SHA-2 precompile calculations based on 1024-byte source data")]
    SHA2From1024Bytes,

    [TestCaseMetadata("SHA-2 precompile from 16k bytes", "SHA-2 precompile calculations based on 16_384-byte source data")]
    SHA2From16KBytes,

    [TestCaseMetadata("Ripemd-160 precompile from 1 byte", "Ripemd-160 precompile calculations based on 1-byte source data")]
    RipemdFrom1Byte,

    [TestCaseMetadata("Ripemd-160 precompile from 8 bytes", "Ripemd-160 precompile calculations based on 8-byte source data")]
    RipemdFrom8Bytes,

    [TestCaseMetadata("Ripemd-160 precompile from 32 bytes", "Ripemd-160 precompile calculations based on 32-byte source data")]
    RipemdFrom32Bytes,

    [TestCaseMetadata("Ripemd-160 precompile from 128 bytes", "Ripemd-160 precompile calculations based on 128-byte source data")]
    RipemdFrom128Bytes,

    [TestCaseMetadata("Ripemd-160 precompile from 1024 bytes", "Ripemd-160 precompile calculations based on 1024-byte source data")]
    RipemdFrom1024Bytes,

    [TestCaseMetadata("Ripemd-160 precompile from 16k bytes", "Ripemd-160 precompile calculations based on 16_384-byte source data")]
    RipemdFrom16KBytes,

    [TestCaseMetadata("Identity precompile from 1 byte", "Identity precompile call based on 1-byte source data")]
    IdentityFrom1Byte,

    [TestCaseMetadata("Identity precompile from 8 bytes", "Identity precompile call based on 8-byte source data")]
    IdentityFrom8Bytes,

    [TestCaseMetadata("Identity precompile from 32 bytes", "Identity precompile call based on 32-byte source data")]
    IdentityFrom32Bytes,

    [TestCaseMetadata("Identity precompile from 128 bytes", "Identity precompile call based on 128-byte source data")]
    IdentityFrom128Bytes,

    [TestCaseMetadata("Identity precompile from 1024 bytes", "Identity precompile call based on 1024-byte source data")]
    IdentityFrom1024Bytes,

    [TestCaseMetadata("Identity precompile from 16k bytes", "Identity precompile call based on 16_384-byte source data")]
    IdentityFrom16KBytes,

    [TestCaseMetadata("Modexp min gas, base heavy", "Modexp precompile consuming 200 gas (minimum value), with base and modulo byte size equal 192 and exponent equal 3 (0b11 - 2x 1s in binary)")]
    ModexpMinGasBaseHeavy,

    [TestCaseMetadata("Modexp min gas, exp heavy", "Modexp precompile consuming 200 gas (minimum value), with base and modulo byte size equal 8 and exponent equal 2^603 - 1 (603x 1s in binary)")]
    ModexpMinGasExpHeavy,

    [TestCaseMetadata("Modexp min gas, balanced", "Modexp precompile consuming 200 gas (minimum value), with base and modulo byte size equal 40 and exponent equal 2^25 - 1 (25x 1s in binary)")]
    ModexpMinGasBalanced,

    [TestCaseMetadata("Modexp 215 gas, exp heavy", "Modexp precompile consuming 215 gas, with base and modulo byte size equal 8 and exponent equal 2^648 - 1 (648x 1s in binary, which is max possible exponent value)")]
    Modexp215GasExpHeavy,

    [TestCaseMetadata("EcAdd with (0, 0)", "EcAdd precompile with both initial points with x = 0 and y = 0")]
    EcAddInfinities,

    [TestCaseMetadata("EcAdd with (1, 2)", "EcAdd precompile with both initial points with x = 1 and y = 2")]
    EcAdd12,

    [TestCaseMetadata("EcAdd with 32-byte coordinates", "EcAdd precompile with both initial points with x and y as 32-byte values")]
    EcAdd32ByteCoordinates,

    [TestCaseMetadata("EcMul with (0, 0) and scalar 2", "EcMul precompile with initial point with x = 0 and y = 0 and scalar equal 2")]
    EcMulInfinities2Scalar,

    [TestCaseMetadata("EcMul with (0, 0) and 32-byte scalar", "EcMul precompile with initial point with x = 0 and y = 0 and scalar as 32-byte values")]
    EcMulInfinities32ByteScalar,

    [TestCaseMetadata("EcMul with (1, 2) and scalar 2", "EcMul precompile with initial point x = 1, y = 2 and scalar equal 2")]
    EcMul122,

    [TestCaseMetadata("EcMul with (1, 2) and 32-byte scalar", "EcMul precompile with initial point x = 1, y = 2 and scalar as 32-byte values")]
    EcMul12And32ByteScalar,

    [TestCaseMetadata("EcMul with 32-byte coordinates and scalar 2", "EcMul precompile with initial point with x and y as 32-byte values and scalar equal 2")]
    EcMul32ByteCoordinates2Scalar,

    [TestCaseMetadata("EcMul with 32-byte coordinates and 32-byte scalar", "EcMul precompile with initial point with x, y and scalar as 32-byte values")]
    EcMul32ByteCoordinates32ByteScalar,

    [TestCaseMetadata("EcPairing with empty input", "EcPairing precompile with empty input")]
    EcPairing0Input,

    [TestCaseMetadata("EcPairing with 2 sets of data", "EcPairing precompile with 2 sets of valid input data (6x 32-byte value)")]
    EcPairing2Sets,

    [TestCaseMetadata("Blake2f 1 round", "Blake2f precompile with 1 round of computations")]
    Blake1Round,

    [TestCaseMetadata("Blake2f 1k rounds", "Blake2f precompile with 1000 rounds of computations")]
    Blake1KRounds,

    [TestCaseMetadata("Blake2f 1M rounds", "Blake2f precompile with 1_000_000 rounds of computations")]
    Blake1MRounds,

    [TestCaseMetadata("Blake2f 10M rounds", "Blake2f precompile with 10_000_000 rounds of computations")]
    Blake10MRounds,

    [TestCaseMetadata("Point evaluation - one data", "Point evaluation precompile repeating computations on the same data")]
    PointEvaluationOneData,

    [TestCaseMetadata("TStore - one storage key, repeating zero value", "TStore - repeating storing zero in single key of transient storage")]
    TStoreOneKeyZeroValue,

    [TestCaseMetadata("TStore - one storage key, repeating constant value", "TStore - repeating storing the same 32-byte word in single single key of transient storage")]
    TStoreOneKeyConstantValue,

    [TestCaseMetadata("TStore - one storage key, repeating random values", "TStore - repeating storing random 32-byte values in single key of transient storage")]
    TStoreOneKeyRandomValue,

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

    [TestCaseMetadata("Secp256r1 precompile, valid signature", "Secp256r1 precompile calculations with valid signature")]
    Secp256r1ValidSignature,

    [TestCaseMetadata("Secp256r1 precompile, invalid signature", "Secp256r1 precompile calculations with invalid signature")]
    Secp256r1InvalidSignature,

    [TestCaseMetadata("Vulnerability Guido 1", "Potential vulnerability reported by Guido, 1")]
    VulnerabilityGuido1,
}
