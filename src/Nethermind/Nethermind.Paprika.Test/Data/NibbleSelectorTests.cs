// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Paprika.Data;
using static Paprika.Data.NibbleSelector;

namespace Paprika.Tests.Data;

[Parallelizable(ParallelScope.Self)]
public class NibbleSelectorTests
{
    private static readonly Type[] Selectors = typeof(NibbleSelector).GetNestedTypes();
    private readonly HashSet<Type> _asserted = [];

    [SetUp]
    public void Setup() => _asserted.Clear();

    [TestCase((byte)0)]
    [TestCase((byte)1)]
    [TestCase((byte)2)]
    [TestCase((byte)3)]
    public void Range_Q0(byte nibble)
    {
        True<All>(nibble);
        True<HalfLow>(nibble);
        True<Q0>(nibble);

        RestIsFalse(nibble);
    }

    [TestCase((byte)4)]
    [TestCase((byte)5)]
    [TestCase((byte)6)]
    [TestCase((byte)7)]
    public void Range_Q1(byte nibble)
    {
        True<All>(nibble);
        True<HalfLow>(nibble);
        True<Q1>(nibble);

        RestIsFalse(nibble);
    }

    [TestCase((byte)8)]
    [TestCase((byte)9)]
    [TestCase((byte)10)]
    [TestCase((byte)11)]
    public void Range_Q2(byte nibble)
    {
        True<All>(nibble);
        True<HalfHigh>(nibble);
        True<Q2>(nibble);

        RestIsFalse(nibble);
    }

    [TestCase((byte)12)]
    [TestCase((byte)13)]
    [TestCase((byte)14)]
    [TestCase((byte)15)]
    public void Range_Q3(byte nibble)
    {
        True<All>(nibble);
        True<HalfHigh>(nibble);
        True<Q3>(nibble);

        RestIsFalse(nibble);
    }

    [Test(Description = "Should throw on non asserted one")]
    public void SanityCheck()
    {
        Exception exception = Assert.Throws<Exception>(() => RestIsFalse(1))!;

        Assert.That(exception.Message, Does.Contain(nameof(All)).And.Contain(nameof(HalfLow)).And.Contain(nameof(Q0)));
    }

    private void RestIsFalse(byte nibble)
    {
        List<Type> failed = [];

        foreach (Type selector in Selectors)
        {
            if (_asserted.Contains(selector))
                continue;

            bool result = (bool)selector.GetMethod(nameof(INibbleSelector.Should))!.Invoke(null, [nibble])!;
            if (result)
            {
                failed.Add(selector);
            }
        }

        _asserted.Clear();

        if (failed.Count > 0)
        {
            throw new Exception(
                "The following selectors returned true while not asserted: " + string.Join(", ", failed));
        }
    }

    private void True<TSelector>(byte nibble) where TSelector : INibbleSelector
    {
        Assert.That(TSelector.Should(nibble), Is.True);
        _asserted.Add(typeof(TSelector));
    }
}
