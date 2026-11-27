// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network.Rlpx
{
    public interface IFrameMacProcessor : IDisposable
    {
        void UpdateEgressMac(ReadOnlySpan<byte> input);

        void UpdateIngressMac(ReadOnlySpan<byte> input, bool isHeader);

        bool CheckMac(ReadOnlySpan<byte> mac, bool isHeader);

        void CalculateMac(Span<byte> mac);

        void AddMac(ReadOnlySpan<byte> input, Span<byte> mac, bool isHeader);
    }
}
