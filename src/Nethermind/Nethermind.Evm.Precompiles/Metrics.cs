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
    public static long Bls12381G1AddPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BLS12_G1MSM precompile calls.")]
    public static long Bls12381G1MsmPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BLS12_G2ADD precompile calls.")]
    public static long Bls12381G2AddPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BLS12_G2MSM precompile calls.")]
    public static long Bls12381G2MsmPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BLS12_PAIRING_CHECK precompile calls.")]
    public static long Bls12381PairingCheckPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BLS12_MAP_FP_TO_G1 precompile calls.")]
    public static long Bls12381FpToG1Precompile { get; set; }

    [CounterMetric]
    [Description("Number of BLS12_MAP_FP2_TO_G2 precompile calls.")]
    public static long Bls12381Fp2ToG2Precompile { get; set; }

    [CounterMetric]
    [Description("Number of ECREC precompile calls.")]
    public static long ECRecoverPrecompile { get; set; }

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
    [Description("Number of P256VERIFY precompile calls.")]
    public static long SecP256r1Precompile { get; set; }

    [CounterMetric]
    [Description("Number of KZG_POINT_EVALUATION precompile calls.")]
    public static long KzgPointEvaluationPrecompile { get; set; }
}
