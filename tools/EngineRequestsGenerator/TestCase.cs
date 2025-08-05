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

    [TestCaseMetadata("Log0 opcode with empty input", "Endlessly emitting empty Log0")]
    Log0Empty,

    [TestCaseMetadata("Log0 opcode with 1-byte input", "Endlessly emitting 1byte Log0")]
    Log01byte,

    [TestCaseMetadata("Log0 opcode with 32-bytes input", "Endlessly emitting 32byte Log0")]
    Log032bytes,

    [TestCaseMetadata("Log0 opcode with 1024-bytes input", "Endlessly emitting 1024-bytes Log0")]
    Log01KiB,

    [TestCaseMetadata("Log0 opcode with 1024-bytes input", "Endlessly emitting 1024-bytes Log0")]
    Log016KiB,

    [TestCaseMetadata("Log4 opcode without data", "Endlessly emitting Log4 without additional data")]
    Log4WithoutData,

    [TestCaseMetadata("Mod1", "")]
    Mod1,

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

    [TestCaseMetadata("Modexp 208 gas, balanced", "Modexp precompile consuming 208 gas, with base and modulo byte size equal 32 and exponent equal 2^40 - 1 (40x 1s in binary)")]
    Modexp208GasBalanced,

    [TestCaseMetadata("Modexp 215 gas, exp heavy", "Modexp precompile consuming 215 gas, with base and modulo byte size equal 8 and exponent equal 2^648 - 1 (648x 1s in binary)")]
    Modexp215GasExpHeavy,

    [TestCaseMetadata("Modexp 298 gas, exp heavy", "Modexp precompile consuming 298 gas, with base and modulo byte size equal 8 and exponent equal 2^896 - 1 (896x 1s in binary)")]
    Modexp298GasExpHeavy,

    [TestCaseMetadata("Modexp Pawel 2", "Modexp precompile consuming 425 gas, with base and modulo byte size equal 16 and exponent equal 2^320 - 1 (320x 1s in binary)")]
    ModexpPawel2,

    [TestCaseMetadata("Modexp Pawel 3", "Modexp precompile consuming 318 gas, with base and modulo byte size equal 16 and exponent equal 2^240 - 1 (240x 1s in binary)")]
    ModexpPawel3,

    [TestCaseMetadata("Modexp Pawel 4", "Modexp precompile consuming 506 gas, with base and modulo byte size equal 32 and exponent equal 2^96 - 1 (96x 1s in binary)")]
    ModexpPawel4,

    [TestCaseMetadata("Modexp 408 gas, base heavy", "Modexp precompile consuming 408 gas, with base and modulo byte size equal 280 and exponent equal 3 (0b11 - 2x 1s in binary)")]
    Modexp408GasBaseHeavy,

    [TestCaseMetadata("Modexp 400 gas, exp heavy", "Modexp precompile consuming 400 gas, with base and modulo byte size equal 16 and exponent equal 2^301 - 1 (301x 1s in binary)")]
    Modexp400GasExpHeavy,

    [TestCaseMetadata("Modexp 408 gas, balanced", "Modexp precompile consuming 408 gas, with base and modulo byte size equal 48 and exponent equal 2^35 - 1 (35x 1s in binary)")]
    Modexp408GasBalanced,

    [TestCaseMetadata("Modexp 616 gas, base heavy", "Modexp precompile consuming 616 gas, with base and modulo byte size equal 344 and exponent equal 3 (0b11 - 2x 1s in binary)")]
    Modexp616GasBaseHeavy,

    [TestCaseMetadata("Modexp 600 gas, exp heavy", "Modexp precompile consuming 600 gas, with base and modulo byte size equal 16 and exponent equal 2^451 - 1 (451x 1s in binary)")]
    Modexp600GasExpHeavy,

    [TestCaseMetadata("Modexp 600 gas, balanced", "Modexp precompile consuming 600 gas, with base and modulo byte size equal 48 and exponent equal 2^51 - 1 (51x 1s in binary)")]
    Modexp600GasBalanced,

    [TestCaseMetadata("Modexp 800 gas, base heavy", "Modexp precompile consuming 800 gas, with base and modulo byte size equal 392 and exponent equal 3 (0b11 - 2x 1s in binary)")]
    Modexp800GasBaseHeavy,

    [TestCaseMetadata("Modexp 800 gas, exp heavy", "Modexp precompile consuming 800 gas, with base and modulo byte size equal 16 and exponent equal 2^601 - 1 (601x 1s in binary)")]
    Modexp800GasExpHeavy,

    [TestCaseMetadata("Modexp 767 gas, balanced", "Modexp precompile consuming 767 gas, with base and modulo byte size equal 56 and exponent equal 2^48 - 1 (48x 1s in binary)")]
    Modexp767GasBalanced,

    [TestCaseMetadata("Modexp 852 gas, exp heavy", "Modexp precompile consuming 852 gas, with base and modulo byte size equal 16 and exponent equal 2^640 - 1 (640x 1s in binary)")]
    Modexp852GasExpHeavy,

    [TestCaseMetadata("Modexp 867 gas, base heavy", "Modexp precompile consuming 867 gas, with base and modulo byte size equal 408 and exponent equal 3 (0b11 - 2x 1s in binary)")]
    Modexp867GasBaseHeavy,

    [TestCaseMetadata("Modexp 996 gas, balanced", "Modexp precompile consuming 996 gas, with base and modulo byte size equal 56 and exponent equal 2^63 - 1 (63x 1s in binary)")]
    Modexp996GasBalanced,

    [TestCaseMetadata("Modexp 1045 gas, base heavy", "Modexp precompile consuming 1045 gas, with base and modulo byte size equal 448 and exponent equal 3 (0b11 - 2x 1s in binary)")]
    Modexp1045GasBaseHeavy,

    [TestCaseMetadata("Modexp 677 gas, balanced", "Modexp precompile consuming 677 gas, with base and modulo byte size equal 32 and exponent equal 2^128 - 1 (128x 1s in binary)")]
    Modexp677GasBaseHeavy,

    [TestCaseMetadata("Modexp 765 gas, balanced", "Modexp precompile consuming 765 gas, with base and modulo byte size equal 24 and exponent equal 2^256 - 1 (256x 1s in binary)")]
    Modexp765GasExpHeavy,

    [TestCaseMetadata("Modexp 1360 gas, balanced", "Modexp precompile consuming 1360 gas, with base and modulo byte size equal 32 and exponent equal 2^256 - 1 (256x 1s in binary)")]
    Modexp1360GasBalanced,

    [TestCaseMetadata("", "")]
    ModexpMod8Exp648,

    [TestCaseMetadata("", "")]
    ModexpMod8Exp896,

    [TestCaseMetadata("", "")]
    ModexpMod32Exp32,

    [TestCaseMetadata("", "")]
    ModexpMod32Exp36,

    [TestCaseMetadata("", "")]
    ModexpMod32Exp40,

    [TestCaseMetadata("", "")]
    ModexpMod32Exp64,

    [TestCaseMetadata("", "")]
    ModexpMod32Exp65,

    [TestCaseMetadata("", "")]
    ModexpMod32Exp128,

    [TestCaseMetadata("", "")]
    ModexpMod256Exp2,

    [TestCaseMetadata("", "")]
    ModexpMod264Exp2,

    [TestCaseMetadata("", "")]
    ModexpMod1024Exp2,

    [TestCaseMetadata("Modexp \"eip_example1\"", "Modexp precompile test case \"eip_example1\" reported as potential vulnerability")]
    ModexpVulnerabilityExample1,

    [TestCaseMetadata("Modexp \"eip_example2\"", "Modexp precompile test case \"eip_example2\" reported as potential vulnerability")]
    ModexpVulnerabilityExample2,

    [TestCaseMetadata("Modexp \"nagydani-1-square\"", "Modexp precompile test case \"nagydani-1-square\" reported as potential vulnerability")]
    ModexpVulnerabilityNagydani1Square,

    [TestCaseMetadata("Modexp \"nagydani-1-qube\"", "Modexp precompile test case \"nagydani-1-qube\" reported as potential vulnerability")]
    ModexpVulnerabilityNagydani1Qube,

    [TestCaseMetadata("Modexp \"nagydani-1-pow0x10001\"", "Modexp precompile test case \"nagydani-1-pow0x10001\" reported as potential vulnerability")]
    ModexpVulnerabilityNagydani1Pow0x10001,

    [TestCaseMetadata("Modexp \"nagydani-2-square\"", "Modexp precompile test case \"nagydani-2-square\" reported as potential vulnerability")]
    ModexpVulnerabilityNagydani2Square,

    [TestCaseMetadata("Modexp \"nagydani-2-qube\"", "Modexp precompile test case \"nagydani-2-qube\" reported as potential vulnerability")]
    ModexpVulnerabilityNagydani2Qube,

    [TestCaseMetadata("Modexp \"nagydani-2-pow0x10001\"", "Modexp precompile test case \"nagydani-2-pow0x10001\" reported as potential vulnerability")]
    ModexpVulnerabilityNagydani2Pow0x10001,

    [TestCaseMetadata("Modexp \"nagydani-3-square\"", "Modexp precompile test case \"nagydani-3-square\" reported as potential vulnerability")]
    ModexpVulnerabilityNagydani3Square,

    [TestCaseMetadata("Modexp \"nagydani-3-qube\"", "Modexp precompile test case \"nagydani-3-qube\" reported as potential vulnerability")]
    ModexpVulnerabilityNagydani3Qube,

    [TestCaseMetadata("Modexp \"nagydani-3-pow0x10001\"", "Modexp precompile test case \"nagydani-3-pow0x10001\" reported as potential vulnerability")]
    ModexpVulnerabilityNagydani3Pow0x10001,

    [TestCaseMetadata("Modexp \"nagydani-4-square\"", "Modexp precompile test case \"nagydani-4-square\" reported as potential vulnerability")]
    ModexpVulnerabilityNagydani4Square,

    [TestCaseMetadata("Modexp \"nagydani-4-qube\"", "Modexp precompile test case \"nagydani-4-qube\" reported as potential vulnerability")]
    ModexpVulnerabilityNagydani4Qube,

    [TestCaseMetadata("Modexp \"nagydani-4-pow0x10001\"", "Modexp precompile test case \"nagydani-4-pow0x10001\" reported as potential vulnerability")]
    ModexpVulnerabilityNagydani4Pow0x10001,

    [TestCaseMetadata("Modexp \"nagydani-5-square\"", "Modexp precompile test case \"nagydani-5-square\" reported as potential vulnerability")]
    ModexpVulnerabilityNagydani5Square,

    [TestCaseMetadata("Modexp \"nagydani-5-qube\"", "Modexp precompile test case \"nagydani-5-qube\" reported as potential vulnerability")]
    ModexpVulnerabilityNagydani5Qube,

    [TestCaseMetadata("Modexp \"nagydani-5-pow0x10001\"", "Modexp precompile test case \"nagydani-5-pow0x10001\" reported as potential vulnerability")]
    ModexpVulnerabilityNagydani5Pow0x10001,

    [TestCaseMetadata("Modexp \"marius-1-even\"", "Modexp precompile test case \"marius-1-even\" reported as potential vulnerability")]
    ModexpVulnerabilityMarius1Even,

    [TestCaseMetadata("Modexp \"guido-1-even\"", "Modexp precompile test case \"guido-1-even\" reported as potential vulnerability")]
    ModexpVulnerabilityGuido1Even,

    [TestCaseMetadata("Modexp \"guido-2-even\"", "Modexp precompile test case \"guido-2-even\" reported as potential vulnerability")]
    ModexpVulnerabilityGuido2Even,

    [TestCaseMetadata("Modexp \"guido-3-even\"", "Modexp precompile test case \"guido-3-even\" reported as potential vulnerability")]
    ModexpVulnerabilityGuido3Even,

    [TestCaseMetadata("Modexp \"guido-4-even\"", "Modexp precompile test case \"guido-4-even\" reported as potential vulnerability")]
    ModexpVulnerabilityGuido4Even,

    [TestCaseMetadata("Modexp \"pawel-1-exp-heavy\"", "Modexp precompile test case \"pawel-1-exp-heavy\" reported as potential vulnerability")]
    ModexpVulnerabilityPawel1ExpHeavy,

    [TestCaseMetadata("Modexp \"pawel-2-exp-heavy\"", "Modexp precompile test case \"pawel-2-exp-heavy\" reported as potential vulnerability")]
    ModexpVulnerabilityPawel2ExpHeavy,

    [TestCaseMetadata("Modexp \"pawel-3-exp-heavy\"", "Modexp precompile test case \"pawel-3-exp-heavy\" reported as potential vulnerability")]
    ModexpVulnerabilityPawel3ExpHeavy,

    [TestCaseMetadata("Modexp \"pawel-4-exp-heavy\"", "Modexp precompile test case \"pawel-4-exp-heavy\" reported as potential vulnerability")]
    ModexpVulnerabilityPawel4ExpHeavy,

    [TestCaseMetadata("Modexp \"zkevm worst-modexp\"", "Modexp precompile test case \"zkevm worst-modexp\" reported as potential vulnerability")]
    ModexpVulnerabilityZkevmWorst,

    [TestCaseMetadata("Modexp common 1360 1", "Modexp precompile test case collected from Mainnet, consuming 1360 gas (base and modulo byte size 32 and exponent bit length 256)")]
    ModexpCommon1360n1,

    [TestCaseMetadata("Modexp common 1360 2", "Modexp precompile test case collected from Mainnet, consuming 1360 gas (base and modulo byte size 32 and exponent bit length 256)")]
    ModexpCommon1360n2,

    [TestCaseMetadata("Modexp common 1349 1", "Modexp precompile test case collected from Mainnet, consuming 200 gas (base and modulo byte size 32 and exponent bit length 254)")]
    ModexpCommon1349n1,

    [TestCaseMetadata("Modexp common 1152 1", "Modexp precompile test case collected from Mainnet, consuming 200 gas (base and modulo byte size 32 and exponent bit length 217)")]
    ModexpCommon1152n1,

    [TestCaseMetadata("Modexp common 200 1", "Modexp precompile test case collected from Mainnet, consuming 200 gas (base and modulo byte size 32 and exponent bit length 25)")]
    ModexpCommon200n1,

    [TestCaseMetadata("Modexp common 200 2", "Modexp precompile test case collected from Mainnet, consuming 200 gas (base and modulo byte size 32 and exponent bit length 25)")]
    ModexpCommon200n2,

    [TestCaseMetadata("Modexp common 200 3", "Modexp precompile test case collected from Mainnet, consuming 200 gas (base and modulo byte size 32 and exponent bit length 25)")]
    ModexpCommon200n3,

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

    [TestCaseMetadata("EcPairing with 2 sets of data", "EcPairing precompile with 2 sets of valid input data (6x 32-byte value)")]
    EcPairing1Pair,

    [TestCaseMetadata("EcPairing with 2 sets of data", "EcPairing precompile with 2 sets of valid input data (6x 32-byte value)")]
    EcPairing2Pairs,

    [TestCaseMetadata("EcPairing with 2 sets of data", "EcPairing precompile with 2 sets of valid input data (6x 32-byte value)")]
    EcPairing3Pairs,

    [TestCaseMetadata("EcPairing with 2 sets of data", "EcPairing precompile with 2 sets of valid input data (6x 32-byte value)")]
    EcPairing4Pairs,

    [TestCaseMetadata("EcPairing with 2 sets of data", "EcPairing precompile with 2 sets of valid input data (6x 32-byte value)")]
    EcPairing5Pairs,

    [TestCaseMetadata("EcPairing with 2 sets of data", "EcPairing precompile with 2 sets of valid input data (6x 32-byte value)")]
    EcPairing10Pairs,

    [TestCaseMetadata("EcPairing with 2 unique sets of data", "EcPairing precompile with 2 sets of valid input data (6x 32-byte value), different for every call")]
    EcPairing2SetsUnique,

    [TestCaseMetadata("Blake2f 1 round", "Blake2f precompile with 1 round of computations")]
    Blake1Round,

    [TestCaseMetadata("Blake2f 1k rounds", "Blake2f precompile with 1000 rounds of computations")]
    Blake1KRounds,

    [TestCaseMetadata("Blake2f 1M rounds", "Blake2f precompile with 1_000_000 rounds of computations")]
    Blake1MRounds,

    [TestCaseMetadata("Blake2f 10M rounds", "Blake2f precompile with 10_000_000 rounds of computations")]
    Blake10MRounds,

    [TestCaseMetadata("Blake2f 1000M rounds", "Blake2f precompile with 1_000_000_000 rounds of computations")]
    Blake1000MRounds,

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
}
