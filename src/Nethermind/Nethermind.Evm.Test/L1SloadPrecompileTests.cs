// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class L1SloadPrecompileTests
{
    private L1SloadPrecompile _precompile = null!;
    private IReleaseSpec _spec = null!;

    [SetUp]
    public void Setup()
    {
        _precompile = L1SloadPrecompile.Instance;
        _spec = new ReleaseSpec { IsL1SloadEnabled = true };
    }

    [Test]
    public void BaseGasCost_Should_Return_2000()
    {
        Assert.That(_precompile.BaseGasCost(_spec), Is.EqualTo(L1SloadPrecompile.FixedGasCost));
    }

    [Test]
    public void DataGasCost_With_Invalid_Input_Length_Should_Return_0()
    {
        // Input too short (less than expected length)
        var input = new byte[L1SloadPrecompile.AddressBytes]; // Only address
        Assert.That(_precompile.DataGasCost(input, _spec), Is.EqualTo(0L));

        // Input too long (more than expected length)
        var expectedLength = L1SloadPrecompile.AddressBytes + L1SloadPrecompile.StorageKeyBytes + L1SloadPrecompile.BlockNumberBytes;
        var longInput = new byte[expectedLength + 32]; // Extra 32 bytes
        Assert.That(_precompile.DataGasCost(longInput, _spec), Is.EqualTo(0L));
    }

    [Test]
    public void DataGasCost_With_Valid_Input_Should_Calculate_Correctly()
    {
        var input = CreateValidInput(Address.FromNumber(123), (UInt256)1, (UInt256)1000);
        Assert.That(_precompile.DataGasCost(input, _spec), Is.EqualTo(L1SloadPrecompile.PerLoadGasCost));
    }

    [Test]
    public void Run_With_Invalid_Input_Length_Should_Fail()
    {
        // Input too short
        var input = new byte[L1SloadPrecompile.AddressBytes];
        var (result, success) = _precompile.Run(input, _spec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);

        // Input too long
        var expectedLength = L1SloadPrecompile.AddressBytes + L1SloadPrecompile.StorageKeyBytes + L1SloadPrecompile.BlockNumberBytes;
        var longInput = new byte[expectedLength + 32];
        (result, success) = _precompile.Run(longInput, _spec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Run_With_Valid_Input_Should_Succeed()
    {
        // Setup mock L1 storage provider
        var expectedValue = (UInt256)0x123456789abcdef;
        L1SloadPrecompile.L1StorageProvider = MockL1StorageProvider.Returning(expectedValue);

        try
        {
            var input = CreateValidInput(Address.FromNumber(123), (UInt256)1, (UInt256)1000);

            var (result, success) = _precompile.Run(input, _spec);

            Assert.That(success, Is.True);
            Assert.That(result.Length, Is.EqualTo(32)); // Single storage value (32 bytes)

            var returnedValue = new UInt256(result.AsSpan(0, 32), isBigEndian: true);
            Assert.That(returnedValue, Is.EqualTo(expectedValue));
        }
        finally
        {
            L1SloadPrecompile.L1StorageProvider = null;
        }
    }

    [Test]
    public void Run_With_Disabled_Spec_Should_Fail()
    {
        var disabledSpec = new ReleaseSpec { IsL1SloadEnabled = false };

        var input = CreateValidInput(Address.FromNumber(123), (UInt256)1, (UInt256)1000);

        var (result, success) = _precompile.Run(input, disabledSpec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Run_With_No_Provider_Should_Fail()
    {
        L1SloadPrecompile.L1StorageProvider = null;
        var input = CreateValidInput(Address.FromNumber(123), (UInt256)1, (UInt256)1000);

        var (result, success) = _precompile.Run(input, _spec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Run_With_Provider_Returning_Null_Should_Fail()
    {
        L1SloadPrecompile.L1StorageProvider = MockL1StorageProvider.ReturningNull();

        try
        {
            var input = CreateValidInput(Address.FromNumber(123), (UInt256)1, (UInt256)1000);

            var (result, success) = _precompile.Run(input, _spec);

            Assert.That(success, Is.False);
            Assert.That(result, Is.Empty);
        }
        finally
        {
            L1SloadPrecompile.L1StorageProvider = null;
        }
    }

    private static byte[] CreateValidInput(Address contractAddress, UInt256 storageKey, UInt256 blockNumber)
    {
        var input = new byte[L1SloadPrecompile.AddressBytes + L1SloadPrecompile.StorageKeyBytes + L1SloadPrecompile.BlockNumberBytes];

        // Copy contract address
        contractAddress.Bytes.CopyTo(input.AsSpan(0, L1SloadPrecompile.AddressBytes));

        // Copy storage key
        storageKey.ToBigEndian().CopyTo(input.AsSpan(L1SloadPrecompile.AddressBytes, L1SloadPrecompile.StorageKeyBytes));

        // Copy block number
        blockNumber.ToBigEndian().CopyTo(input.AsSpan(L1SloadPrecompile.AddressBytes + L1SloadPrecompile.StorageKeyBytes, L1SloadPrecompile.BlockNumberBytes));

        return input;
    }

    private sealed class MockL1StorageProvider : IL1StorageProvider
    {
        private readonly UInt256? _returnValue;

        private MockL1StorageProvider(UInt256? returnValue) => _returnValue = returnValue;

        public UInt256? GetStorageValue(Address contractAddress, UInt256 storageKey, UInt256 blockNumber) => _returnValue;

        // Static factory methods for different scenarios
        public static MockL1StorageProvider Returning(UInt256 value) => new(value);
        public static MockL1StorageProvider ReturningNull() => new(null);
    }
}
