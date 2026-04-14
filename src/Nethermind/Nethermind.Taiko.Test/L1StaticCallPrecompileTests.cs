// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Taiko.Precompiles;
using Nethermind.Taiko.TaikoSpec;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

[TestFixture]
public class L1StaticCallPrecompileTests
{
    private const long MockGasUsed = 5000L;
    private const long TestRemainingGas = 1_000_000L;

    private L1StaticCallPrecompile _precompile = null!;
    private IReleaseSpec _spec = null!;

    [SetUp]
    public void Setup()
    {
        _precompile = L1StaticCallPrecompile.Instance;
        _spec = new TaikoReleaseSpec { IsL1StaticCallEnabled = true, TaikoL2Address = Address.Zero };
        L1StaticCallPrecompile.Logger = default;
    }

    [TearDown]
    public void TearDown() => L1StaticCallPrecompile.L1CallProvider = null;

    // --- BaseGasCost ---

    [Test]
    public void BaseGasCost_Should_Return_FixedGasCost() => Assert.That(_precompile.BaseGasCost(_spec), Is.EqualTo(L1PrecompileConstants.L1StaticCallFixedGasCost));

    // --- DataGasCost (static overhead only) ---

    [Test]
    public void DataGasCost_With_Short_Input_Should_Return_0()
    {
        byte[] input = new byte[Address.Size]; // 20 bytes, well below 52
        Assert.That(_precompile.DataGasCost(input, _spec), Is.EqualTo(0L));
    }

    [Test]
    public void DataGasCost_With_Min_Input_Should_Return_PerCallOverhead()
    {
        byte[] input = CreateValidInput(Address.FromNumber(1), (UInt256)100);

        Assert.That(_precompile.DataGasCost(input, _spec), Is.EqualTo(L1PrecompileConstants.L1StaticCallPerCallOverhead));
    }

    [Test]
    public void DataGasCost_With_Variable_Calldata_Should_Include_PerByte()
    {
        byte[] calldata = [0xAA, 0xBB, 0xCC, 0xDD];
        byte[] input = CreateValidInput(Address.FromNumber(1), (UInt256)100, calldata);

        long expected = L1PrecompileConstants.L1StaticCallPerCallOverhead
                      + L1PrecompileConstants.L1StaticCallPerByteCalldataCost * 4; // 10000 + 64
        Assert.That(_precompile.DataGasCost(input, _spec), Is.EqualTo(expected));
    }

    // --- Gas limiting ---

    [Test]
    public void Run_Should_Use_RemainingGas_When_Less_Than_GasCap()
    {
        MockL1CallProvider mock = MockL1CallProvider.Returning(new byte[32], MockGasUsed);
        L1StaticCallPrecompile.L1CallProvider = mock;

        byte[] input = CreateValidInput(Address.FromNumber(1), (UInt256)1);
        _precompile.Run(input, _spec, 50_000);

        Assert.That(mock.LastGasLimit, Is.EqualTo(50_000));
    }

    [Test]
    public void Run_Should_Use_GasCap_When_Less_Than_RemainingGas()
    {
        MockL1CallProvider mock = MockL1CallProvider.Returning(new byte[32], MockGasUsed);
        L1StaticCallPrecompile.L1CallProvider = mock;

        byte[] input = CreateValidInput(Address.FromNumber(1), (UInt256)1);
        _precompile.Run(input, _spec, 100_000_000);

        Assert.That(mock.LastGasLimit, Is.EqualTo(L1PrecompileConstants.L1CallMaxGasCap));
    }

    [Test]
    public void Run_Should_Clamp_GasLimit_To_Zero_When_RemainingGas_Is_Zero()
    {
        MockL1CallProvider mock = MockL1CallProvider.Returning(new byte[32], MockGasUsed);
        L1StaticCallPrecompile.L1CallProvider = mock;
        byte[] input = CreateValidInput(Address.FromNumber(1), (UInt256)1);

        _precompile.Run(input, _spec, 0);

        Assert.That(mock.LastGasLimit, Is.EqualTo(0));
    }

    // --- Run (gas-aware) ---

    [Test]
    public void Run_With_Short_Input_Should_Fail()
    {
        byte[] input = new byte[Address.Size]; // 20 bytes, below 52

        Result<(byte[] returnValue, long gasConsumed)> result = _precompile.Run(input, _spec, TestRemainingGas);

        Assert.That(result.IsSuccess, Is.False);
    }

    [Test]
    public void Run_With_Valid_Input_Should_Succeed()
    {
        byte[] expectedReturn = [0x00, 0x01, 0x02, 0x03];
        L1StaticCallPrecompile.L1CallProvider = MockL1CallProvider.Returning(expectedReturn, MockGasUsed);

        byte[] calldata = [0xDE, 0xAD];
        byte[] input = CreateValidInput(Address.FromNumber(42), (UInt256)1000, calldata);

        Result<(byte[] returnValue, long gasConsumed)> result = _precompile.Run(input, _spec, TestRemainingGas);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Data.returnValue, Is.EqualTo(expectedReturn));
        Assert.That(result.Data.gasConsumed, Is.EqualTo(MockGasUsed));
    }

    [Test]
    public void Run_With_No_Provider_Should_Fail_With_Zero_Gas()
    {
        L1StaticCallPrecompile.L1CallProvider = null;
        byte[] input = CreateValidInput(Address.FromNumber(1), (UInt256)1);

        Result<(byte[] returnValue, long gasConsumed)> result = _precompile.Run(input, _spec, TestRemainingGas);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Data.gasConsumed, Is.EqualTo(0));
    }

    [Test]
    public void Run_With_Provider_Failing_Should_Report_GasConsumed()
    {
        long l1GasUsed = 12_000L;
        L1StaticCallPrecompile.L1CallProvider = MockL1CallProvider.FailingWithGas(l1GasUsed);
        byte[] input = CreateValidInput(Address.FromNumber(1), (UInt256)1);

        Result<(byte[] returnValue, long gasConsumed)> result = _precompile.Run(input, _spec, TestRemainingGas);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Data.gasConsumed, Is.EqualTo(l1GasUsed));
    }

    [Test]
    public void Run_Should_Call_Provider_Exactly_Once()
    {
        MockL1CallProvider mock = MockL1CallProvider.Returning([0x01], MockGasUsed);
        L1StaticCallPrecompile.L1CallProvider = mock;
        byte[] input = CreateValidInput(Address.FromNumber(1), (UInt256)1);

        _precompile.Run(input, _spec, TestRemainingGas);

        Assert.That(mock.CallCount, Is.EqualTo(1));
    }

    [TestCase(0, true, Description = "Empty return data")]
    [TestCase(32, true, Description = "Single ABI word")]
    [TestCase(L1PrecompileConstants.L1StaticCallMaxReturnDataSize, true, Description = "Exactly at limit")]
    [TestCase(L1PrecompileConstants.L1StaticCallMaxReturnDataSize + 1, false, Description = "Over limit")]
    public void Run_With_Variable_Length_Return_Data(int returnSize, bool expectedSuccess)
    {
        byte[] returnData = new byte[returnSize];
        L1StaticCallPrecompile.L1CallProvider = MockL1CallProvider.Returning(returnData, MockGasUsed);
        byte[] input = CreateValidInput(Address.FromNumber(1), (UInt256)1);

        Result<(byte[] returnValue, long gasConsumed)> result = _precompile.Run(input, _spec, TestRemainingGas);

        Assert.That(result.IsSuccess, Is.EqualTo(expectedSuccess));
        Assert.That(result.Data.gasConsumed, Is.EqualTo(MockGasUsed));
        if (expectedSuccess)
            Assert.That(result.Data.returnValue.Length, Is.EqualTo(returnSize));
    }

    [Test]
    public void DataGasCost_Then_Run_EndToEnd()
    {
        byte[] expectedReturn = [0xCA, 0xFE];
        long mockGas = 8000L;
        L1StaticCallPrecompile.L1CallProvider = MockL1CallProvider.Returning(expectedReturn, mockGas);

        byte[] calldata = [0x5C, 0x97, 0x5A, 0xBB]; // paused() selector
        byte[] input = CreateValidInput(Address.FromNumber(99), (UInt256)500, calldata);

        // DataGasCost returns only static overhead
        long gasCost = _precompile.DataGasCost(input, _spec);
        long expectedStaticGas = L1PrecompileConstants.L1StaticCallPerCallOverhead
                               + L1PrecompileConstants.L1StaticCallPerByteCalldataCost * 4; // 10000 + 64
        Assert.That(gasCost, Is.EqualTo(expectedStaticGas));

        // Run returns result + actual L1 gas consumed
        Result<(byte[] returnValue, long gasConsumed)> result = _precompile.Run(input, _spec, TestRemainingGas);
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Data.returnValue, Is.EqualTo(expectedReturn));
        Assert.That(result.Data.gasConsumed, Is.EqualTo(mockGas));
    }

    // --- IPrecompile.Run fallback ---

    [Test]
    public void Run_IPrecompile_Fallback_Should_Delegate_To_GasAware()
    {
        byte[] expectedReturn = [0x01, 0x02];
        L1StaticCallPrecompile.L1CallProvider = MockL1CallProvider.Returning(expectedReturn, MockGasUsed);
        byte[] input = CreateValidInput(Address.FromNumber(1), (UInt256)1);

        // Call via IPrecompile interface
        IPrecompile precompile = _precompile;
        Result<byte[]> result = precompile.Run(input, _spec);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Data, Is.EqualTo(expectedReturn));
    }

    // --- Spec flag ---

    [Test]
    public void IsPrecompile_Active_With_L1StaticCall()
    {
        IReleaseSpec enabledSpec = new TaikoReleaseSpec { IsL1StaticCallEnabled = true, TaikoL2Address = Address.Zero };
        IReleaseSpec disabledSpec = new TaikoReleaseSpec { IsL1StaticCallEnabled = false, TaikoL2Address = Address.Zero };

        Address precompileAddress = L1StaticCallPrecompile.Address;

        Assert.That(enabledSpec.IsPrecompile(precompileAddress), Is.True,
            "L1StaticCallPrecompile address should be identified as precompile when L1StaticCall is enabled");

        Assert.That(disabledSpec.IsPrecompile(precompileAddress), Is.False,
            "L1StaticCallPrecompile address should not be identified as precompile when L1StaticCall is disabled");
    }

    // --- Helpers ---

    private static byte[] CreateValidInput(Address contractAddress, UInt256 blockNumber, byte[]? calldata = null)
    {
        int calldataLen = calldata?.Length ?? 0;
        byte[] input = new byte[L1PrecompileConstants.L1StaticCallMinInputLength + calldataLen];
        contractAddress.Bytes.CopyTo(input.AsSpan(0, Address.Size));
        blockNumber.ToBigEndian().CopyTo(input.AsSpan(Address.Size, L1PrecompileConstants.BlockNumberBytes));
        if (calldata is not null)
            calldata.CopyTo(input.AsSpan(L1PrecompileConstants.L1StaticCallMinInputLength));
        return input;
    }

    private sealed class MockL1CallProvider : IL1CallProvider
    {
        private readonly L1CallResult _result;

        public int CallCount { get; private set; }
        public long LastGasLimit { get; private set; }

        private MockL1CallProvider(L1CallResult result) => _result = result;

        public L1CallResult ExecuteTraceCall(Address contractAddress, UInt256 blockNumber, byte[] calldata, long gasLimit)
        {
            CallCount++;
            LastGasLimit = gasLimit;
            return _result;
        }

        public static MockL1CallProvider Returning(byte[] data, long gasUsed = 0) =>
            new(new L1CallResult(data, gasUsed, false));

        public static MockL1CallProvider Failing() =>
            new(L1CallResult.Failure());

        public static MockL1CallProvider FailingWithGas(long gasUsed) =>
            new(new L1CallResult(null, gasUsed, true));
    }
}
