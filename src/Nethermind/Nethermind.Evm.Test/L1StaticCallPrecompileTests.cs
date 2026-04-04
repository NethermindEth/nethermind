// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class L1StaticCallPrecompileTests
{
    private L1StaticCallPrecompile _precompile = null!;
    private IReleaseSpec _spec = null!;

    [SetUp]
    public void Setup()
    {
        _precompile = L1StaticCallPrecompile.Instance;
        _spec = new ReleaseSpec { IsL1StaticCallEnabled = true };
        // ILogger is a struct; default value has all Is* = false, so logging is safely no-op.
        L1StaticCallPrecompile.Logger = default;
    }

    [TearDown]
    public void TearDown()
    {
        L1StaticCallPrecompile.L1CallProvider = null;
    }

    [Test]
    public void BaseGasCost_Should_Return_FixedGasCost()
    {
        Assert.That(_precompile.BaseGasCost(_spec), Is.EqualTo(L1PrecompileConstants.L1StaticCallFixedGasCost));
    }

    [Test]
    public void DataGasCost_With_Short_Input_Should_Return_0()
    {
        byte[] input = new byte[L1PrecompileConstants.AddressBytes]; // 20 bytes, well below 52
        Assert.That(_precompile.DataGasCost(input, _spec), Is.EqualTo(0L));
    }

    [Test]
    public void DataGasCost_With_Min_Input_Should_Calculate_Correctly()
    {
        // Exactly 52 bytes (address + blockNumber, no calldata) -> overhead only
        byte[] input = CreateValidInput(Address.FromNumber(1), (UInt256)100);
        Assert.That(input.Length, Is.EqualTo(L1PrecompileConstants.L1StaticCallMinInputLength));

        long expected = L1PrecompileConstants.L1StaticCallPerCallOverhead; // 10000
        Assert.That(_precompile.DataGasCost(input, _spec), Is.EqualTo(expected));
    }

    [Test]
    public void DataGasCost_With_Variable_Calldata_Should_Include_PerByte()
    {
        // 56 bytes = 52 min + 4 bytes calldata -> 10000 + 16*4 = 10064
        byte[] calldata = [0xAA, 0xBB, 0xCC, 0xDD];
        byte[] input = CreateValidInput(Address.FromNumber(1), (UInt256)100, calldata);
        Assert.That(input.Length, Is.EqualTo(L1PrecompileConstants.L1StaticCallMinInputLength + 4));

        long expected = L1PrecompileConstants.L1StaticCallPerCallOverhead
                      + L1PrecompileConstants.L1StaticCallPerByteCalldataCost * 4; // 10000 + 64 = 10064
        Assert.That(_precompile.DataGasCost(input, _spec), Is.EqualTo(expected));
    }

    [Test]
    public void Run_With_Short_Input_Should_Fail()
    {
        byte[] input = new byte[L1PrecompileConstants.AddressBytes]; // 20 bytes, below 52

        (byte[] result, bool success) = _precompile.Run(input, _spec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Run_With_Valid_Input_Should_Succeed()
    {
        byte[] expectedReturn = [0x00, 0x01, 0x02, 0x03];
        L1StaticCallPrecompile.L1CallProvider = MockL1CallProvider.Returning(expectedReturn);

        byte[] calldata = [0xDE, 0xAD];
        byte[] input = CreateValidInput(Address.FromNumber(42), (UInt256)1000, calldata);

        (byte[] result, bool success) = _precompile.Run(input, _spec);

        Assert.That(success, Is.True);
        Assert.That(result, Is.EqualTo(expectedReturn));
    }

    [Test]
    public void Run_With_No_Provider_Should_Fail()
    {
        L1StaticCallPrecompile.L1CallProvider = null;

        byte[] input = CreateValidInput(Address.FromNumber(1), (UInt256)1);

        (byte[] result, bool success) = _precompile.Run(input, _spec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Run_With_Provider_Returning_Null_Should_Fail()
    {
        L1StaticCallPrecompile.L1CallProvider = MockL1CallProvider.ReturningNull();

        byte[] input = CreateValidInput(Address.FromNumber(1), (UInt256)1);

        (byte[] result, bool success) = _precompile.Run(input, _spec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);
    }

    [TestCase(0, true, Description = "Empty return data")]
    [TestCase(32, true, Description = "Single ABI word")]
    [TestCase(L1PrecompileConstants.L1StaticCallMaxReturnDataSize, true, Description = "Exactly at limit")]
    [TestCase(L1PrecompileConstants.L1StaticCallMaxReturnDataSize + 1, false, Description = "Over limit")]
    public void Run_With_Variable_Length_Return_Data(int returnSize, bool expectedSuccess)
    {
        byte[] returnData = new byte[returnSize];
        L1StaticCallPrecompile.L1CallProvider = MockL1CallProvider.Returning(returnData);
        byte[] input = CreateValidInput(Address.FromNumber(1), (UInt256)1);

        (byte[] result, bool success) = _precompile.Run(input, _spec);

        Assert.That(success, Is.EqualTo(expectedSuccess));
        if (expectedSuccess)
            Assert.That(result.Length, Is.EqualTo(returnSize));
        else
            Assert.That(result, Is.Empty);
    }

    [Test]
    public void IsPrecompile_Active_With_L1StaticCall()
    {
        IReleaseSpec enabledSpec = new ReleaseSpec { IsL1StaticCallEnabled = true };
        IReleaseSpec disabledSpec = new ReleaseSpec { IsL1StaticCallEnabled = false };

        Address precompileAddress = L1StaticCallPrecompile.Address;

        Assert.That(enabledSpec.IsPrecompile(precompileAddress), Is.True,
            "L1StaticCallPrecompile address should be identified as precompile when L1StaticCall is enabled");

        Assert.That(disabledSpec.IsPrecompile(precompileAddress), Is.False,
            "L1StaticCallPrecompile address should not be identified as precompile when L1StaticCall is disabled");
    }

    private static byte[] CreateValidInput(Address target, UInt256 blockNumber, byte[]? calldata = null)
    {
        int calldataLen = calldata?.Length ?? 0;
        byte[] input = new byte[L1PrecompileConstants.L1StaticCallMinInputLength + calldataLen];
        target.Bytes.CopyTo(input.AsSpan(0, L1PrecompileConstants.AddressBytes));
        blockNumber.ToBigEndian().CopyTo(input.AsSpan(L1PrecompileConstants.AddressBytes, L1PrecompileConstants.BlockNumberBytes));
        if (calldata is not null)
            calldata.CopyTo(input.AsSpan(L1PrecompileConstants.L1StaticCallMinInputLength));
        return input;
    }

    private sealed class MockL1CallProvider : IL1CallProvider
    {
        private readonly byte[]? _returnValue;

        private MockL1CallProvider(byte[]? returnValue)
        {
            _returnValue = returnValue;
        }

        public byte[]? ExecuteStaticCall(Address target, UInt256 blockNumber, byte[] calldata) => _returnValue;

        public static MockL1CallProvider Returning(byte[] data) => new(data);
        public static MockL1CallProvider ReturningNull() => new(null);
    }
}
