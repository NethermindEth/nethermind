// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Taiko.Precompiles;
using Nethermind.Taiko.TaikoSpec;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

[TestFixture]
public class L1SloadPrecompileTests
{
    private L1SloadPrecompile _precompile = null!;
    private IReleaseSpec _spec = null!;

    [SetUp]
    public void Setup()
    {
        _precompile = L1SloadPrecompile.Instance;
        _spec = new TaikoReleaseSpec { IsRip7728Enabled = true, TaikoL2Address = Address.Zero };
        // ILogger is a struct; default value has all Is* = false, so logging is safely no-op.
        L1SloadPrecompile.Logger = default;
    }

    [TearDown]
    public void TearDown() => L1SloadPrecompile.L1StorageProvider = null;

    [Test]
    public void BaseGasCost_Should_Return_FixedGasCost() => Assert.That(_precompile.BaseGasCost(_spec), Is.EqualTo(L1PrecompileConstants.L1SloadFixedGasCost));

    [Test]
    public void DataGasCost_With_Invalid_Input_Length_Should_Return_0()
    {
        // Input too short
        byte[] input = new byte[Address.Size]; // Only address
        Assert.That(_precompile.DataGasCost(input, _spec), Is.EqualTo(0L));

        // Input too long
        byte[] longInput = new byte[L1PrecompileConstants.L1SloadExpectedInputLength + 32];
        Assert.That(_precompile.DataGasCost(longInput, _spec), Is.EqualTo(0L));
    }

    [Test]
    public void DataGasCost_With_Valid_Input_Should_Calculate_Correctly()
    {
        byte[] input = CreateValidInput(Address.FromNumber(123), storageKey: (UInt256)1, blockNumber: (UInt256)1000);
        Assert.That(_precompile.DataGasCost(input, _spec), Is.EqualTo(L1PrecompileConstants.L1SloadPerLoadGasCost));
    }

    [Test]
    public void Run_With_Invalid_Input_Length_Should_Fail()
    {
        // Input too short
        byte[] input = new byte[Address.Size];
        (byte[]? result, bool success) = _precompile.Run(input, _spec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);

        // Input too long
        byte[] longInput = new byte[L1PrecompileConstants.L1SloadExpectedInputLength + 32];
        (result, success) = _precompile.Run(longInput, _spec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Run_With_Valid_Input_Should_Succeed()
    {
        UInt256 expectedValue = (UInt256)0x123456789abcdef;
        L1SloadPrecompile.L1StorageProvider = MockL1StorageProvider.Returning(expectedValue);

        byte[] input = CreateValidInput(Address.FromNumber(123), storageKey: (UInt256)1, blockNumber: (UInt256)1000);

        (byte[]? result, bool success) = _precompile.Run(input, _spec);

        Assert.That(success, Is.True);
        Assert.That(result!.Length, Is.EqualTo(32));

        UInt256 returnedValue = new(result!.AsSpan(0, 32), isBigEndian: true);
        Assert.That(returnedValue, Is.EqualTo(expectedValue));
    }

    [Test]
    public void Run_With_No_Provider_Should_Fail()
    {
        L1SloadPrecompile.L1StorageProvider = null;
        byte[] input = CreateValidInput(Address.FromNumber(123), storageKey: (UInt256)1, blockNumber: (UInt256)1000);

        (byte[]? result, bool success) = _precompile.Run(input, _spec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Run_With_Provider_Returning_Null_Should_Fail()
    {
        L1SloadPrecompile.L1StorageProvider = MockL1StorageProvider.ReturningNull();

        byte[] input = CreateValidInput(Address.FromNumber(123), storageKey: (UInt256)1, blockNumber: (UInt256)1000);

        (byte[]? result, bool success) = _precompile.Run(input, _spec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void IsPrecompile_Active_With_Rip7728()
    {
        IReleaseSpec enabledSpec = new TaikoReleaseSpec { IsRip7728Enabled = true, TaikoL2Address = Address.Zero };
        IReleaseSpec disabledSpec = new TaikoReleaseSpec { IsRip7728Enabled = false, TaikoL2Address = Address.Zero };

        Address? precompileAddress = L1SloadPrecompile.Address;

        Assert.That(enabledSpec.IsPrecompile(precompileAddress), Is.True,
            "L1SloadPrecompile address should be identified as precompile when RIP-7728 is enabled");

        Assert.That(disabledSpec.IsPrecompile(precompileAddress), Is.False,
            "L1SloadPrecompile address should not be identified as precompile when RIP-7728 is disabled");
    }

    // --- Block-range validation (l1Origin passed as argument) ---

    [TestCase(1000ul, 700ul, false, Description = "Block 700 is 300 away from l1Origin 1000 — exceeds 256 lookback")]
    [TestCase(1000ul, 744ul, true, Description = "Block 744 is exactly 256 from l1Origin 1000 — should be accepted")]
    [TestCase(1000ul, 743ul, false, Description = "Block 743 is 257 from l1Origin 1000 — one past the inclusive boundary")]
    [TestCase(1000ul, 1000ul, true, Description = "Block 1000 == l1Origin 1000 — upper inclusive edge")]
    [TestCase(1000ul, 1001ul, false, Description = "Block 1001 > l1Origin 1000 — must be rejected")]
    public void Run_BlockRangeValidation(ulong l1Origin, ulong blockNumber, bool expectedSuccess)
    {
        L1SloadPrecompile.L1StorageProvider = MockL1StorageProvider.Returning((UInt256)42);
        byte[] input = CreateValidInput(Address.FromNumber(1), storageKey: (UInt256)1, blockNumber: (UInt256)blockNumber);

        PrecompileExtras extras = new(l1Origin: (UInt256)l1Origin);
        Result<(byte[] returnValue, ulong gasConsumed)> result = _precompile.Run(input, _spec, in extras);

        Assert.That(result.IsSuccess, Is.EqualTo(expectedSuccess));
    }

    [Test]
    public void Run_NullOrigin_AcceptsAnyBlock()
    {
        L1SloadPrecompile.L1StorageProvider = MockL1StorageProvider.Returning((UInt256)42);
        byte[] input = CreateValidInput(Address.FromNumber(1), storageKey: (UInt256)1, blockNumber: (UInt256)12_345);

        Result<(byte[] returnValue, ulong gasConsumed)> result = _precompile.Run(input, _spec, in PrecompileExtras.None);

        Assert.That(result.IsSuccess, Is.True, "Permissive when no origin is available (eth_call / debug_traceCall / preconf)");
    }

    /// <summary>
    /// Parameter order matches the wire layout [address | storageKey | blockNumber] so callers
    /// constructing test bytes don't have to mentally invert anything.
    /// </summary>
    private static byte[] CreateValidInput(Address contractAddress, UInt256 storageKey, UInt256 blockNumber)
    {
        byte[] input = new byte[L1PrecompileConstants.L1SloadExpectedInputLength];

        contractAddress.Bytes.CopyTo(input.AsSpan(0, Address.Size));
        storageKey.ToBigEndian().CopyTo(input.AsSpan(Address.Size, L1PrecompileConstants.L1SloadStorageKeyBytes));
        blockNumber.ToBigEndian().CopyTo(input.AsSpan(Address.Size + L1PrecompileConstants.L1SloadStorageKeyBytes, L1PrecompileConstants.BlockNumberBytes));

        return input;
    }

    private sealed class MockL1StorageProvider : IL1StorageProvider
    {
        private readonly UInt256? _returnValue;

        private MockL1StorageProvider(UInt256? returnValue) => _returnValue = returnValue;

        public UInt256? GetStorageValue(Address contractAddress, UInt256 blockNumber, UInt256 storageKey) => _returnValue;

        public static MockL1StorageProvider Returning(UInt256 value) => new(value);
        public static MockL1StorageProvider ReturningNull() => new(null);
    }
}
