// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Bls;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class BlsPairingCheckPrecompileTests
{
    [Test]
    public void Test()
    {
        foreach ((byte[] input, ReadOnlyMemory<byte> expectedResult) in Inputs)
        {
            IPrecompile precompile = PairingCheckPrecompile.Instance;
            (ReadOnlyMemory<byte> output, bool success) = precompile.Run(input, MuirGlacier.Instance);

            output.ToArray().Should().BeEquivalentTo(expectedResult.ToArray());
            success.Should().BeTrue();
        }
    }

    /// <summary>
    /// https://github.com/matter-labs/eip1962/tree/master/src/test/test_vectors/eip2537
    /// </summary>
    private static readonly Dictionary<byte[], ReadOnlyMemory<byte>> Inputs = new()
    {
        { Bytes.FromHexString("0000000000000000000000000000000012196c5a43d69224d8713389285f26b98f86ee910ab3dd668e413738282003cc5b7357af9a7af54bb713d62255e80f560000000000000000000000000000000006ba8102bfbeea4416b710c73e8cce3032c31c6269c44906f8ac4f7874ce99fb17559992486528963884ce429a992fee0000000000000000000000000000000017c9fcf0504e62d3553b2f089b64574150aa5117bd3d2e89a8c1ed59bb7f70fb83215975ef31976e757abf60a75a1d9f0000000000000000000000000000000008f5a53d704298fe0cfc955e020442874fe87d5c729c7126abbdcbed355eef6c8f07277bee6d49d56c4ebaf334848624000000000000000000000000000000001302dcc50c6ce4c28086f8e1b43f9f65543cf598be440123816765ab6bc93f62bceda80045fbcad8598d4f32d03ee8fa000000000000000000000000000000000bbb4eb37628d60b035a3e0c45c0ea8c4abef5a6ddc5625e0560097ef9caab208221062e81cd77ef72162923a1906a40"), Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000000") }};
}
