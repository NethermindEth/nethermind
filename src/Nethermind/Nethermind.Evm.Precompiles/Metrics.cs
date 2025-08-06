// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Evm.Precompiles;

public class Metrics
{
    [CounterMetric]
    [Description("Number of BN254_MUL precompile calls.")]
    public static long Bn254MulPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BN254_ADD precompile calls.")]
    public static long Bn254AddPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BN254_PAIRING precompile calls.")]
    public static long Bn254PairingPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BLS12_G1ADD precompile calls.")]
    public static long BlsG1AddPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BLS12_G1MUL precompile calls.")]
    public static long BlsG1MulPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BLS12_G1MSM precompile calls.")]
    public static long BlsG1MSMPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BLS12_G2ADD precompile calls.")]
    public static long BlsG2AddPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BLS12_G2MUL precompile calls.")]
    public static long BlsG2MulPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BLS12_G2MSM precompile calls.")]
    public static long BlsG2MSMPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BLS12_PAIRING_CHECK precompile calls.")]
    public static long BlsPairingCheckPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BLS12_MAP_FP_TO_G1 precompile calls.")]
    public static long BlsMapFpToG1Precompile { get; set; }

    [CounterMetric]
    [Description("Number of BLS12_MAP_FP2_TO_G2 precompile calls.")]
    public static long BlsMapFp2ToG2Precompile { get; set; }

    [CounterMetric]
    [Description("Number of EC_RECOVERY precompile calls.")]
    public static long EcRecoverPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of MODEXP precompile calls.")]
    public static long ModExpPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of RIPEMD160 precompile calls.")]
    public static long Ripemd160Precompile { get; set; }

    [CounterMetric]
    [Description("Number of SHA256 precompile calls.")]
    public static long Sha256Precompile { get; set; }

    [CounterMetric]
    [Description("Number of Secp256r1 precompile calls.")]
    public static long Secp256r1Precompile { get; set; }

    [CounterMetric]
    [Description("Number of Point Evaluation precompile calls.")]
    public static long PointEvaluationPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of L1SLOAD precompile calls.")]
    public static long L1SloadPrecompile { get; set; }
}
