// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;

public class NativeCallTracerLogEntry(
    Address address,
    byte[] data,
    Hash256[] topics,
    ulong index,
    ulong position)
{
    public Address Address { get; init; } = address;
    public byte[] Data { get; init; } = data;
    public Hash256[] Topics { get; init; } = topics;
    /// <summary>The log's index within the block; logs of reverted frames consume none.</summary>
    /// <remarks>Equals the receipt <c>logIndex</c> only before EIP-8116, which restarts that index per receipt.</remarks>
    public ulong Index { get; init; } = index;
    public ulong Position { get; init; } = position;
}
