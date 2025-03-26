// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark;

public class Secp256r1Benchmark : PrecompileBenchmarkBase
{
    protected override IEnumerable<IPrecompile> Precompiles =>
    [
        Secp256r1Precompile.Instance
    ];

    public override IEnumerable<Param> Inputs
    {
        get
        {
            yield return new Param(
                Secp256r1Precompile.Instance,
                "Valid1",
                Convert.FromHexString("4cee90eb86eaa050036147a12d49004b6b9c72bd725d39d4785011fe190f0b4da73bd4903f0ce3b639bbbf6e8e80d16931ff4bcf5993d58468e8fb19086e8cac36dbcd03009df8c59286b162af3bd7fcc0450c9aa81be5d10d312af6c66b1d604aebd3099c618202fcfe16ae7770b0c49ab5eadf74b754204a3bb6060e44eff37618b065f9832de4ca6ca971a7a1adc826d0f7c00181a5fb2ddf79ae00b4e10e"),
                []
            );

            yield return new Param(
                Secp256r1Precompile.Instance,
                "Valid2",
                Convert.FromHexString("bb5a52f42f9c9261ed4361f59422a1e30036e7c32b270c8807a419feca60502300000000000000000000000000000000000000000000000000000000000001008f1e3c7862c58b16bb76eddbb76eddbb516af4f63f2d74d76e0d28c9bb75ea88d0f73792203716afd4be4329faa48d269f15313ebbba379d7783c97bf3e890d9971f4a3206605bec21782bf5e275c714417e8f566549e6bc68690d2363c89cc1"),
                []
            );

            yield return new Param(
                Secp256r1Precompile.Instance,
                "Valid3",
                Convert.FromHexString("fc28259702a03845b6d75219444e8b43d094586e249c8699ffffffffe852512e0671a0a85c2b72d54a2fb0990e34538b4890050f5a5712f6d1a7a5fb8578f32edb1846bab6b7361479ab9c3285ca41291808f27fd5bd4fdac720e5854713694c2927b10512bae3eddcfe467828128bad2903269919f7086069c8c4df6c732838c7787964eaac00e5921fb1498a60f4606766b3d9685001558d1a974e7341513e"),
                []
            );

            yield return new Param(
                Secp256r1Precompile.Instance,
                "Invalid",
                Convert.FromHexString("3cee90eb86eaa050036147a12d49004b6b9c72bd725d39d4785011fe190f0b4da73bd4903f0ce3b639bbbf6e8e80d16931ff4bcf5993d58468e8fb19086e8cac36dbcd03009df8c59286b162af3bd7fcc0450c9aa81be5d10d312af6c66b1d604aebd3099c618202fcfe16ae7770b0c49ab5eadf74b754204a3bb6060e44eff37618b065f9832de4ca6ca971a7a1adc826d0f7c00181a5fb2ddf79ae00b4e10e"),
                []
            );
        }
    }

    protected override string InputsDirectory => throw new NotSupportedException();
}
