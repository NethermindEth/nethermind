// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// L1SLOAD precompile - read storage values from L1 contracts (RIP-7728).
///
/// The input to the L1SLOAD precompile consists of:
///
/// | Byte range          | Name               | Description                      |
/// | ------------------  | ------------------ | -------------------------------  |
/// | [0: 19] (20 bytes)  | address            | The L1 contract address          |
/// | [20: 51] (32 bytes) | storageKey         | The storage key to read          |
/// | [52: 83] (32 bytes) | blockNumber        | The L1 block number to read from |
///
/// Output:
/// - Storage value (32 bytes)
/// </summary>
public class L1SloadPrecompile : IPrecompile<L1SloadPrecompile>
{
    public static readonly L1SloadPrecompile Instance = new();

    public const int AddressBytes = 20;
    public const int StorageKeyBytes = 32;
    public const int BlockNumberBytes = 32;
    public const long FixedGasCost = 2000L;
    public const long PerLoadGasCost = 2000L;
    public const int ExpectedInputLength = AddressBytes + StorageKeyBytes + BlockNumberBytes;

    private L1SloadPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x12);
    public static string Name => "L1SLOAD";
    public static IL1StorageProvider? L1StorageProvider { get; set; }

    public long BaseGasCost(IReleaseSpec releaseSpec) => FixedGasCost;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length != ExpectedInputLength)
        {
            return 0L;
        }

        return PerLoadGasCost;
    }

    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (!releaseSpec.IsL1SloadEnabled)
        {
            return IPrecompile.Failure;
        }

        if (inputData.Length != ExpectedInputLength)
        {
            return IPrecompile.Failure;
        }

        var contractAddress = new Address(inputData.Span[..AddressBytes]);
        var storageKey = new UInt256(inputData.Span[AddressBytes..(AddressBytes + StorageKeyBytes)], isBigEndian: true);
        var blockNumber = new UInt256(inputData.Span[(AddressBytes + StorageKeyBytes)..], isBigEndian: true);

        var storageValue = GetL1StorageValue(contractAddress, storageKey, blockNumber);
        if (storageValue == null)
        {
            return IPrecompile.Failure; // L1 storage access failed
        }

        // Convert storage value to output bytes
        var output = new byte[32];
        storageValue.Value.ToBigEndian().CopyTo(output.AsSpan());

        return (output, true);
    }

    /// <summary>
    /// Retrieves L1 storage value for the specified contract address, storage key, and block number.
    /// </summary>
    /// <param name="contractAddress">The L1 contract address to read storage from</param>
    /// <param name="storageKey">The storage key to read from the L1 contract</param>
    /// <param name="blockNumber">The L1 block number to read the storage state from</param>
    /// <returns>The storage value, or null if access fails</returns>
    private UInt256? GetL1StorageValue(Address contractAddress, UInt256 storageKey, UInt256 blockNumber)
    {
        try
        {
            return L1StorageProvider?.GetStorageValue(contractAddress, storageKey, blockNumber);
        }
        catch
        {
            return null;
        }
    }
}
