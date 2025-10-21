// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Logging;
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
        _spec = new ReleaseSpec { IsRip7728Enabled = true };
    }

    [Test]
    public void BaseGasCost_Should_Return_FixedGasCost()
    {
        Assert.That(_precompile.BaseGasCost(_spec), Is.EqualTo(L1PrecompileConstants.FixedGasCost));
    }

    [Test]
    public void DataGasCost_With_Invalid_Input_Length_Should_Return_0()
    {
        // Input too short
        var input = new byte[L1PrecompileConstants.AddressBytes]; // Only address
        Assert.That(_precompile.DataGasCost(input, _spec).Data, Is.EqualTo(0L));

        // Input too long
        var expectedLength = L1PrecompileConstants.AddressBytes + L1PrecompileConstants.StorageKeyBytes + L1PrecompileConstants.BlockNumberBytes;
        var longInput = new byte[expectedLength + 32]; // Extra 32 bytes
        Assert.That(_precompile.DataGasCost(longInput, _spec).Data, Is.EqualTo(0L));
    }

    [Test]
    public void DataGasCost_With_Valid_Input_Should_Calculate_Correctly()
    {
        var input = CreateValidInput(Address.FromNumber(123), (UInt256)1, (UInt256)1000);
        Assert.That(_precompile.DataGasCost(input, _spec).Data, Is.EqualTo(L1PrecompileConstants.PerLoadGasCost));
    }

    [Test]
    public void Run_With_Invalid_Input_Length_Should_Fail()
    {
        // Input too short
        var input = new byte[L1PrecompileConstants.AddressBytes];
        var (result, success) = _precompile.Run(input, _spec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);

        // Input too long
        var expectedLength = L1PrecompileConstants.AddressBytes + L1PrecompileConstants.StorageKeyBytes + L1PrecompileConstants.BlockNumberBytes;
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
        var disabledSpec = new ReleaseSpec { IsRip7728Enabled = false };

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

    [Test]
    public void IsPrecompile_Active_With_Rip7728()
    {
        IReleaseSpec enabledSpec = new ReleaseSpec { IsRip7728Enabled = true };
        IReleaseSpec disabledSpec = new ReleaseSpec { IsRip7728Enabled = false };

        Address? precompileAddress = L1SloadPrecompile.Address;

        Assert.That(enabledSpec.IsPrecompile(precompileAddress), Is.True,
            "L1SloadPrecompile address should be identified as precompile when RIP-7728 is enabled");

        Assert.That(disabledSpec.IsPrecompile(precompileAddress), Is.False,
            "L1SloadPrecompile address should not be identified as precompile when RIP-7728 is disabled");
    }

    private static byte[] CreateValidInput(Address contractAddress, UInt256 storageKey, UInt256 blockNumber)
    {
        var input = new byte[L1PrecompileConstants.AddressBytes + L1PrecompileConstants.StorageKeyBytes + L1PrecompileConstants.BlockNumberBytes];

        contractAddress.Bytes.CopyTo(input.AsSpan(0, L1PrecompileConstants.AddressBytes));
        storageKey.ToBigEndian().CopyTo(input.AsSpan(L1PrecompileConstants.AddressBytes, L1PrecompileConstants.StorageKeyBytes));
        blockNumber.ToBigEndian().CopyTo(input.AsSpan(L1PrecompileConstants.AddressBytes + L1PrecompileConstants.StorageKeyBytes, L1PrecompileConstants.BlockNumberBytes));

        return input;
    }

    private sealed class MockL1StorageProvider : IL1StorageProvider
    {
        private readonly UInt256? _returnValue;

        private MockL1StorageProvider(UInt256? returnValue)
        {
            _returnValue = returnValue;
        }

        public UInt256? GetStorageValue(Address contractAddress, UInt256 storageKey, UInt256 blockNumber) => _returnValue;

        // Static factory methods for different scenarios
        public static MockL1StorageProvider Returning(UInt256 value) => new(value);
        public static MockL1StorageProvider ReturningNull() => new(null);
    }
}
