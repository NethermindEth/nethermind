// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
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
        { Lp = 1, FusakaEnabled = true, BaseLength = 32, ExpLength = 32, ModulusLength = 32, Result = 4080L };
        yield return new Eip7883TestCase
        { Lp = 2, FusakaEnabled = true, BaseLength = 32, ExpLength = 32, ModulusLength = 1024, Result = 8355840L };
        yield return new Eip7883TestCase
        { Lp = 3, FusakaEnabled = true, BaseLength = 32, ExpLength = 1024, ModulusLength = 32, Result = 258032L };
        yield return new Eip7883TestCase
        { Lp = 4, FusakaEnabled = true, BaseLength = 1024, ExpLength = 32, ModulusLength = 32, Result = 8355840L };
        yield return new Eip7883TestCase
        { Lp = 5, FusakaEnabled = true, BaseLength = 32, ExpLength = 1024, ModulusLength = 1024, Result = 528449536L };
        yield return new Eip7883TestCase
        { Lp = 6, FusakaEnabled = true, BaseLength = 10000, ExpLength = 1024, ModulusLength = 32, Result = long.MaxValue };
        yield return new Eip7883TestCase
        { Lp = 7, FusakaEnabled = true, BaseLength = 1024, ExpLength = 10000, ModulusLength = 1024, Result = long.MaxValue };
        yield return new Eip7883TestCase
        { Lp = 8, FusakaEnabled = true, BaseLength = 1024, ExpLength = 1024, ModulusLength = 10000, Result = long.MaxValue };
        yield return new Eip7883TestCase        // testing exponent >32bytes
        { Lp = 9, FusakaEnabled = true, BaseLength = 8, ExpLength = 81, ModulusLength = 8, Result = 16624L };
        yield return new Eip7883TestCase        // testing base/modulo below 32 bytes
        { Lp = 10, FusakaEnabled = true, BaseLength = 8, ExpLength = 8, ModulusLength = 8, Result = 1008L };
        yield return new Eip7883TestCase        // testing 3x general pricing mechanism
        { Lp = 11, FusakaEnabled = true, BaseLength = 32, ExpLength = 5, ModulusLength = 32, Result = 624L };
        yield return new Eip7883TestCase        // testing bump of min price
        { Lp = 12, FusakaEnabled = true, BaseLength = 32, ExpLength = 1, ModulusLength = 32, Result = 500L };
        yield return new Eip7883TestCase        // testing base >32bytes
        { Lp = 13, FusakaEnabled = true, BaseLength = 40, ExpLength = 8, ModulusLength = 32, Result = 3150L };
        yield return new Eip7883TestCase        // testing modulo >32bytes
        { Lp = 14, FusakaEnabled = true, BaseLength = 32, ExpLength = 8, ModulusLength = 40, Result = 3150L };
        yield return new Eip7883TestCase        // testing base&modulo >32bytes
        { Lp = 15, FusakaEnabled = true, BaseLength = 40, ExpLength = 8, ModulusLength = 40, Result = 3150L };
        yield return new Eip7883TestCase
        { Lp = 16, FusakaEnabled = true, BaseLength = 0, ExpLength = 34, ModulusLength = 33, Result = 3150L };

        // eip disabled test cases
        yield return new Eip7883TestCase
        { Lp = 101, FusakaEnabled = false, BaseLength = 32, ExpLength = 32, ModulusLength = 32, Result = 1360L };
        yield return new Eip7883TestCase
        { Lp = 102, FusakaEnabled = false, BaseLength = 32, ExpLength = 32, ModulusLength = 10000, Result = 132812500L };
        yield return new Eip7883TestCase
        { Lp = 103, FusakaEnabled = false, BaseLength = 32, ExpLength = 10000, ModulusLength = 32, Result = 426661L };
        yield return new Eip7883TestCase
        { Lp = 104, FusakaEnabled = false, BaseLength = 10000, ExpLength = 32, ModulusLength = 32, Result = 132812500L };
        yield return new Eip7883TestCase
        { Lp = 105, FusakaEnabled = false, BaseLength = 32, ExpLength = 10000, ModulusLength = 10000, Result = 41666145833L };
        yield return new Eip7883TestCase
        { Lp = 106, FusakaEnabled = false, BaseLength = 10000, ExpLength = 10000, ModulusLength = 32, Result = 41666145833L };
        yield return new Eip7883TestCase
        { Lp = 107, FusakaEnabled = false, BaseLength = 10000, ExpLength = 32, ModulusLength = 10000, Result = 132812500L };
        yield return new Eip7883TestCase
        { Lp = 108, FusakaEnabled = false, BaseLength = 10000, ExpLength = 10000, ModulusLength = 10000, Result = 41666145833L };
        yield return new Eip7883TestCase
        { Lp = 109, FusakaEnabled = false, BaseLength = 8, ExpLength = 81, ModulusLength = 8, Result = 215L };
        yield return new Eip7883TestCase
        { Lp = 110, FusakaEnabled = false, BaseLength = 8, ExpLength = 8, ModulusLength = 8, Result = 200L };
        yield return new Eip7883TestCase
        { Lp = 111, FusakaEnabled = false, BaseLength = 32, ExpLength = 5, ModulusLength = 32, Result = 208L };
        yield return new Eip7883TestCase
        { Lp = 112, FusakaEnabled = false, BaseLength = 32, ExpLength = 1, ModulusLength = 32, Result = 200L };
        yield return new Eip7883TestCase
        { Lp = 113, FusakaEnabled = false, BaseLength = 40, ExpLength = 8, ModulusLength = 32, Result = 525L };
        yield return new Eip7883TestCase
        { Lp = 114, FusakaEnabled = false, BaseLength = 32, ExpLength = 8, ModulusLength = 40, Result = 525L };
        yield return new Eip7883TestCase
        { Lp = 115, FusakaEnabled = false, BaseLength = 40, ExpLength = 8, ModulusLength = 40, Result = 525L };
        yield return new Eip7883TestCase
        { Lp = 116, FusakaEnabled = false, BaseLength = 0, ExpLength = 34, ModulusLength = 33, Result = 3150L };
    }

    private static ReadOnlyMemory<byte> PrepareInput(UInt256 baseLength, UInt256 expLength, UInt256 modulusLength)
    {
        var inputBytes = new byte[(int)(96 + baseLength + expLength + modulusLength)];

        Array.Copy(baseLength.ToBigEndian(), 0, inputBytes, 0, 32);
        Array.Copy(expLength.ToBigEndian(), 0, inputBytes, 32, 32);
        Array.Copy(modulusLength.ToBigEndian(), 0, inputBytes, 64, 32);
        Array.Copy(GetInput(baseLength).ToArray(), 0, inputBytes, 96, (int)baseLength);
        Array.Copy(GetInput(expLength).ToArray(), 0, inputBytes, 96 + (int)baseLength, (int)expLength);
        Array.Copy(GetInput(modulusLength).ToArray(), 0, inputBytes, 96 + (int)(baseLength + expLength), (int)modulusLength);

        return new ReadOnlyMemory<byte>(inputBytes);
    }

    private static IEnumerable<byte> GetInput(UInt256 length)
    {
        for (int i = 0; i < length; i++)
        {
            if (length == 34)
            {
                yield return 0x00;
            }
            else
            {
                yield return 0xFF;
            }
        }
    }
}
