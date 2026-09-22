// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;

public class NativeCallTracerLogEntry(
    Address address,
    byte[] data,
    Hash256[] topics,
    ulong position)
{
    public Address Address { get; init; } = address;
    public byte[] Data { get; init; } = data;
    public Hash256[] Topics { get; init; } = topics;
    /// <summary>The zero-based index among the block's surviving logs.</summary>
    public ulong Index { get; internal set; }
    public ulong Position { get; init; } = position;
}
