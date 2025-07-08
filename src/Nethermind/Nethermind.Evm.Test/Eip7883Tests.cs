// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Specs;
using Nethermind.Blockchain.Precompiles;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip7883Tests
{
    [Test]
    public void DataGasCost([ValueSource(nameof(Eip7883TestCases))] Eip7883TestCase test)
    {
        ReadOnlyMemory<byte> inputData = PrepareInput(test.BaseLength, test.ExpLength, test.ModulusLength);

        IReleaseSpec? spec = test.FusakaEnabled ? Osaka.Instance : Prague.Instance;
        long gas = ModExpPrecompile.Instance.DataGasCost(inputData, spec);
        gas.Should().Be(test.Result);
    }

    public class Eip7883TestCase
    {
        public int Lp { get; set; }
        public bool FusakaEnabled { get; set; }
        public UInt256 BaseLength { get; set; }
        public UInt256 ExpLength { get; set; }
        public UInt256 ModulusLength { get; set; }
        public long Result { get; set; }
        public override string ToString()
        {
            return $"Lp: {Lp}, " +
                   $"FusakaEnabled: {FusakaEnabled}, " +
                   $"BaseLength: {BaseLength}, " +
                   $"ExpLength: {ExpLength}, " +
                   $"ModulusLength: {ModulusLength}, " +
                   $"Result: {Result}";
        }
    }

    private static IEnumerable<Eip7883TestCase> Eip7883TestCases()
    {
        // eip enabled test cases
        yield return new Eip7883TestCase
        { Lp = 1, FusakaEnabled = true, BaseLength = 32, ExpLength = 32, ModulusLength = 32, Result = 500L };
        yield return new Eip7883TestCase
        { Lp = 2, FusakaEnabled = true, BaseLength = 32, ExpLength = 32, ModulusLength = 1024, Result = 10922L };
        yield return new Eip7883TestCase
        { Lp = 3, FusakaEnabled = true, BaseLength = 32, ExpLength = 1024, ModulusLength = 32, Result = 84650L };
        yield return new Eip7883TestCase
        { Lp = 4, FusakaEnabled = true, BaseLength = 1024, ExpLength = 32, ModulusLength = 32, Result = 10922L };
        yield return new Eip7883TestCase
        { Lp = 5, FusakaEnabled = true, BaseLength = 32, ExpLength = 1024, ModulusLength = 1024, Result = 173364565L };
        yield return new Eip7883TestCase
        { Lp = 6, FusakaEnabled = true, BaseLength = 10000, ExpLength = 1024, ModulusLength = 32, Result = long.MaxValue };
        yield return new Eip7883TestCase
        { Lp = 7, FusakaEnabled = true, BaseLength = 1024, ExpLength = 10000, ModulusLength = 1024, Result = long.MaxValue };
        yield return new Eip7883TestCase
        { Lp = 8, FusakaEnabled = true, BaseLength = 1024, ExpLength = 1024, ModulusLength = 10000, Result = long.MaxValue };

        // eip disabled test cases
        yield return new Eip7883TestCase
        { Lp = 9, FusakaEnabled = false, BaseLength = 32, ExpLength = 32, ModulusLength = 32, Result = 200L };
        yield return new Eip7883TestCase
        { Lp = 10, FusakaEnabled = false, BaseLength = 32, ExpLength = 32, ModulusLength = 10000, Result = 520833L };
        yield return new Eip7883TestCase
        { Lp = 11, FusakaEnabled = false, BaseLength = 32, ExpLength = 10000, ModulusLength = 32, Result = 425301L };
        yield return new Eip7883TestCase
        { Lp = 12, FusakaEnabled = false, BaseLength = 10000, ExpLength = 32, ModulusLength = 32, Result = 520833L };
        yield return new Eip7883TestCase
        { Lp = 13, FusakaEnabled = false, BaseLength = 32, ExpLength = 10000, ModulusLength = 10000, Result = 41533333333L };
        yield return new Eip7883TestCase
        { Lp = 14, FusakaEnabled = false, BaseLength = 10000, ExpLength = 10000, ModulusLength = 32, Result = 41533333333L };
        yield return new Eip7883TestCase
        { Lp = 15, FusakaEnabled = false, BaseLength = 10000, ExpLength = 32, ModulusLength = 10000, Result = 520833L };
        yield return new Eip7883TestCase
        { Lp = 16, FusakaEnabled = false, BaseLength = 10000, ExpLength = 10000, ModulusLength = 10000, Result = 41533333333L };
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
