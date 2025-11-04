// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// L1CALL precompile - execute calls on L1 contracts (RIP-7728).
///
/// The input to the L1CALL precompile consists of:
///
/// | Byte range                    | Name          | Description                     |
/// | ----------------------------- | ------------- | ------------------------------- |
/// | [0: 7] (8 bytes)              | gas           | The gas limit for the call      |
/// | [8: 27] (20 bytes)            | address       | The L1 contract address         |
/// | [28: 59] (32 bytes)           | value         | The value to send with the call |
/// | [60: 67] (8 bytes)            | callDataSize  | Size of call data in bytes      |
/// | [68: 99] (32 bytes)           | feePerGas     | Fee per gas for L1 call         |
/// | [100: 99+callDataSize]        | callData      | Call data (variable length)     |
///
/// Output:
/// - Return data from the L1 call
/// </summary>
public class L1CallPrecompile : IPrecompile<L1CallPrecompile>
{
    public static readonly L1CallPrecompile Instance = new();

    private L1CallPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x10002);
    public static string Name => "L1CALL";
    public static IL1CallProvider? L1CallProvider { get; set; }

    public long BaseGasCost(IReleaseSpec releaseSpec) => L1CallConstants.FixedGasCost;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        return inputData.Length < L1CallConstants.MinInputLength ? 0L : L1CallConstants.PerCallGasCost;
    }

    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length < L1CallConstants.MinInputLength)
        {
            return IPrecompile.Failure;
        }

        ReadOnlySpan<byte> span = inputData.Span;
        int offset = 0;

        // Parse fixed parameters
        ulong gas = BitConverter.ToUInt64(span.Slice(offset, L1CallConstants.GasBytes));
        offset += L1CallConstants.GasBytes;

        Address contractAddress = new Address(span.Slice(offset, L1CallConstants.AddressBytes));
        offset += L1CallConstants.AddressBytes;

        UInt256 value = new UInt256(span.Slice(offset, L1CallConstants.ValueBytes), isBigEndian: true);
        offset += L1CallConstants.ValueBytes;

        ulong callDataSize = BitConverter.ToUInt64(span.Slice(offset, L1CallConstants.CallDataSizeBytes));
        offset += L1CallConstants.CallDataSizeBytes;

        UInt256 feePerGas = new UInt256(span.Slice(offset, L1CallConstants.FeePerGasBytes), isBigEndian: true);
        offset += L1CallConstants.FeePerGasBytes;

        // Validate total input length (all fixed fields + call data)
        ulong expectedLength = L1CallConstants.MinInputLength + callDataSize;
        if (inputData.Length < (int)expectedLength)
        {
            return IPrecompile.Failure;
        }

        // Extract call data (variable length)
        byte[]? callData = null;
        if (callDataSize > 0)
        {
            callData = span.Slice(offset, (int)callDataSize).ToArray();
        }

        byte[]? returnData = ExecuteL1Call(contractAddress, gas, value, callData, feePerGas);
        if (returnData == null)
        {
            return IPrecompile.Failure; // L1 call execution failed
        }

        return (returnData, true);
    }

    /// <summary>
    /// Executes an L1 call for the specified parameters.
    /// </summary>
    /// <param name="contractAddress">The L1 contract address to call</param>
    /// <param name="gas">The gas limit for the call</param>
    /// <param name="value">The value to send with the call</param>
    /// <param name="callData">The call data (function selector + parameters), or null/empty if no call data</param>
    /// <param name="feePerGas">The fee the user is willing to pay for executing this call on L1</param>
    /// <returns>The return data from the L1 call, or null if the call fails</returns>
    private byte[]? ExecuteL1Call(Address contractAddress, ulong gas, UInt256 value, byte[]? callData, UInt256 feePerGas)
    {
        try
        {
            return L1CallProvider?.ExecuteCall(contractAddress, gas, value, callData, feePerGas);
        }
        catch
        {
            return null;
        }
    }
}
