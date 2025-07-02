// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Shutter;

public interface IShutterEon
{
    Info? GetCurrentEonInfo();

    void Update(BlockHeader header);

    readonly struct Info
    {
        public ulong Eon { get; init; }
        public byte[] Key { get; init; }
        public ulong Threshold { get; init; }
        public Address[] Addresses { get; init; }
    }
}
