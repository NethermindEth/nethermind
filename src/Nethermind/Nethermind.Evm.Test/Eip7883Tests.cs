// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip7883Tests
{
    [Test]
    public void DataGasCost([ValueSource(nameof(Eip7883TestCases))] Eip7883TestCase test)
    {
        var inputData = PrepareInput(test.BaseLength, test.ExpLength, test.ModulusLength);

        long gas = ModExpPrecompile.Instance.DataGasCost(inputData, test.Spec);
        gas.Should().Be(test.Result);
    }

    public class Eip7883TestCase
    {
        public IReleaseSpec Spec { get; set; }
        public UInt256 BaseLength { get; set; }
        public UInt256 ExpLength { get; set; }
        public UInt256 ModulusLength { get; set; }
        public long Result { get; set; }
    }

    private static readonly IReleaseSpec SpecEipEnabled = new ReleaseSpec
    {
        IsEip7883Enabled = true,
        IsEip2565Enabled = true,
    };

    private static readonly IReleaseSpec SpecEipDisabled = new ReleaseSpec
    {
        IsEip7883Enabled = false,
        IsEip2565Enabled = true,
    };

    private static IEnumerable<Eip7883TestCase> Eip7883TestCases()
    {
        // eip enabled test cases
        yield return new Eip7883TestCase
        { Spec = SpecEipEnabled, BaseLength = 32, ExpLength = 32, ModulusLength = 32, Result = 500L };
        yield return new Eip7883TestCase
        { Spec = SpecEipEnabled, BaseLength = 32, ExpLength = 32, ModulusLength = 10000, Result = 1041666L };
        yield return new Eip7883TestCase
        { Spec = SpecEipEnabled, BaseLength = 32, ExpLength = 10000, ModulusLength = 32, Result = 850602L };
        yield return new Eip7883TestCase
        { Spec = SpecEipEnabled, BaseLength = 10000, ExpLength = 32, ModulusLength = 32, Result = 1041666L };
        yield return new Eip7883TestCase
        { Spec = SpecEipEnabled, BaseLength = 32, ExpLength = 10000, ModulusLength = 10000, Result = 166133333333L };
        yield return new Eip7883TestCase
        { Spec = SpecEipEnabled, BaseLength = 10000, ExpLength = 10000, ModulusLength = 32, Result = 166133333333L };
        yield return new Eip7883TestCase
        { Spec = SpecEipEnabled, BaseLength = 10000, ExpLength = 32, ModulusLength = 10000, Result = 1041666L };
        yield return new Eip7883TestCase
        { Spec = SpecEipEnabled, BaseLength = 10000, ExpLength = 10000, ModulusLength = 10000, Result = 166133333333L };

        // eip disabled test cases
        yield return new Eip7883TestCase
        { Spec = SpecEipDisabled, BaseLength = 32, ExpLength = 32, ModulusLength = 32, Result = 200L };
        yield return new Eip7883TestCase
        { Spec = SpecEipDisabled, BaseLength = 32, ExpLength = 32, ModulusLength = 10000, Result = 520833L };
        yield return new Eip7883TestCase
        { Spec = SpecEipDisabled, BaseLength = 32, ExpLength = 10000, ModulusLength = 32, Result = 425301L };
        yield return new Eip7883TestCase
        { Spec = SpecEipDisabled, BaseLength = 10000, ExpLength = 32, ModulusLength = 32, Result = 520833L };
        yield return new Eip7883TestCase
        { Spec = SpecEipDisabled, BaseLength = 32, ExpLength = 10000, ModulusLength = 10000, Result = 41533333333L };
        yield return new Eip7883TestCase
        { Spec = SpecEipDisabled, BaseLength = 10000, ExpLength = 10000, ModulusLength = 32, Result = 41533333333L };
        yield return new Eip7883TestCase
        { Spec = SpecEipDisabled, BaseLength = 10000, ExpLength = 32, ModulusLength = 10000, Result = 520833L };
        yield return new Eip7883TestCase
        { Spec = SpecEipDisabled, BaseLength = 10000, ExpLength = 10000, ModulusLength = 10000, Result = 41533333333L };
    }

    private static ReadOnlyMemory<byte> PrepareInput(UInt256 baseLength, UInt256 expLength, UInt256 modulusLength)
    {
        var inputBytes = new byte[96];

        Array.Copy(baseLength.ToBigEndian(), 0, inputBytes, 0, 32);
        Array.Copy(expLength.ToBigEndian(), 0, inputBytes, 32, 32);
        Array.Copy(modulusLength.ToBigEndian(), 0, inputBytes, 64, 32);

        return new ReadOnlyMemory<byte>(inputBytes);
    }
}
