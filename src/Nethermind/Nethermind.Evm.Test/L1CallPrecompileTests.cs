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
public class L1CallPrecompileTests
{
    private L1CallPrecompile _precompile = null!;
    private IReleaseSpec _spec = null!;

    [SetUp]
    public void Setup()
    {
        _precompile = L1CallPrecompile.Instance;
        _spec = new ReleaseSpec { IsL1CallEnabled = true };
    }

    [Test]
    public void BaseGasCost_Should_Return_FixedGasCost()
    {
        Assert.That(_precompile.BaseGasCost(_spec), Is.EqualTo(L1CallConstants.FixedGasCost));
    }

    [Test]
    public void DataGasCost_With_Invalid_Input_Length_Should_Return_0()
    {
        // Input too short
        var input = new byte[L1CallConstants.AddressBytes]; // Only address
        Assert.That(_precompile.DataGasCost(input, _spec), Is.EqualTo(0L));

        // Input with invalid callDataSize (too long)
        var longInput = new byte[L1CallConstants.MinInputLength + 1000];
        Assert.That(_precompile.DataGasCost(longInput, _spec), Is.EqualTo(L1CallConstants.PerCallGasCost));
    }

    [Test]
    public void DataGasCost_With_Valid_Input_Should_Calculate_Correctly()
    {
        var input = CreateValidInput(Address.FromNumber(123), 1000000, (UInt256)0, null, (UInt256)20000000000);
        Assert.That(_precompile.DataGasCost(input, _spec), Is.EqualTo(L1CallConstants.PerCallGasCost));
    }

    [Test]
    public void Run_With_Invalid_Input_Length_Should_Fail()
    {
        // Input too short
        var input = new byte[L1CallConstants.AddressBytes];
        var (result, success) = _precompile.Run(input, _spec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);

        // Input with callDataSize that exceeds available data
        var invalidInput = new byte[L1CallConstants.MinInputLength];
        // Set callDataSize to 100 but we only have MinInputLength bytes
        BitConverter.GetBytes(100UL).CopyTo(invalidInput.AsSpan(L1CallConstants.GasBytes + L1CallConstants.AddressBytes + L1CallConstants.ValueBytes, L1CallConstants.CallDataSizeBytes));
        (result, success) = _precompile.Run(invalidInput, _spec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Run_With_Valid_Input_Should_Succeed()
    {
        // Setup mock L1 call provider
        var expectedReturnData = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        L1CallPrecompile.L1CallProvider = MockL1CallProvider.Returning(expectedReturnData);

        try
        {
            var input = CreateValidInput(Address.FromNumber(123), 1000000, (UInt256)0, null, (UInt256)20000000000);

            var (result, success) = _precompile.Run(input, _spec);

            Assert.That(success, Is.True);
            Assert.That(result, Is.EqualTo(expectedReturnData));
        }
        finally
        {
            L1CallPrecompile.L1CallProvider = null;
        }
    }

    [Test]
    public void Run_With_Disabled_Spec_Should_Fail()
    {
        var disabledSpec = new ReleaseSpec { IsL1CallEnabled = false };

        var input = CreateValidInput(Address.FromNumber(123), 1000000, (UInt256)0, null, (UInt256)20000000000);

        var (result, success) = _precompile.Run(input, disabledSpec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Run_With_No_Provider_Should_Fail()
    {
        L1CallPrecompile.L1CallProvider = null;
        var input = CreateValidInput(Address.FromNumber(123), 1000000, (UInt256)0, null, (UInt256)20000000000);

        var (result, success) = _precompile.Run(input, _spec);

        Assert.That(success, Is.False);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Run_With_Provider_Returning_Null_Should_Fail()
    {
        L1CallPrecompile.L1CallProvider = MockL1CallProvider.ReturningNull();

        try
        {
            var input = CreateValidInput(Address.FromNumber(123), 1000000, (UInt256)0, null, (UInt256)20000000000);

            var (result, success) = _precompile.Run(input, _spec);

            Assert.That(success, Is.False);
            Assert.That(result, Is.Empty);
        }
        finally
        {
            L1CallPrecompile.L1CallProvider = null;
        }
    }

    [Test]
    public void IsPrecompile_Active_With_L1Call()
    {
        IReleaseSpec enabledSpec = new ReleaseSpec { IsL1CallEnabled = true };
        IReleaseSpec disabledSpec = new ReleaseSpec { IsL1CallEnabled = false };

        Address? precompileAddress = L1CallPrecompile.Address;

        Assert.That(enabledSpec.IsPrecompile(precompileAddress), Is.True,
            "L1CallPrecompile address should be identified as precompile when L1Call is enabled");

        Assert.That(disabledSpec.IsPrecompile(precompileAddress), Is.False,
            "L1CallPrecompile address should not be identified as precompile when L1Call is disabled");
    }

    private static byte[] CreateValidInput(Address contractAddress, ulong gas, UInt256 value, byte[]? callData, UInt256 feePerGas)
    {
        ulong callDataSize = callData == null ? 0UL : (ulong)callData.Length;
        int totalLength = L1CallConstants.MinInputLength + (int)callDataSize;
        var input = new byte[totalLength];

        int offset = 0;

        // Copy gas (8 bytes)
        BitConverter.GetBytes(gas).CopyTo(input.AsSpan(offset, L1CallConstants.GasBytes));
        offset += L1CallConstants.GasBytes;

        // Copy address (20 bytes)
        contractAddress.Bytes.CopyTo(input.AsSpan(offset, L1CallConstants.AddressBytes));
        offset += L1CallConstants.AddressBytes;

        // Copy value (32 bytes)
        value.ToBigEndian().CopyTo(input.AsSpan(offset, L1CallConstants.ValueBytes));
        offset += L1CallConstants.ValueBytes;

        // Copy callDataSize (8 bytes)
        BitConverter.GetBytes(callDataSize).CopyTo(input.AsSpan(offset, L1CallConstants.CallDataSizeBytes));
        offset += L1CallConstants.CallDataSizeBytes;

        // Copy feePerGas (32 bytes)
        feePerGas.ToBigEndian().CopyTo(input.AsSpan(offset, L1CallConstants.FeePerGasBytes));
        offset += L1CallConstants.FeePerGasBytes;

        // Copy callData (variable length, at the end)
        if (callData != null && callData.Length > 0)
        {
            callData.CopyTo(input.AsSpan(offset, callData.Length));
        }

        return input;
    }

    private sealed class MockL1CallProvider : IL1CallProvider
    {
        private readonly byte[]? _returnData;

        private MockL1CallProvider(byte[]? returnData)
        {
            _returnData = returnData;
        }

        public byte[]? ExecuteCall(Address contractAddress, ulong gas, UInt256 value, byte[]? callData, UInt256 feePerGas) => _returnData;

        // Static factory methods for different scenarios
        public static MockL1CallProvider Returning(byte[] returnData) => new(returnData);
        public static MockL1CallProvider ReturningNull() => new(null);
    }
}
