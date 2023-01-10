// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Mev.Data
{
    public class MevMegabundleRpc : MevBundleRpc
    {
        public byte[] RelaySignature { get; set; } = Array.Empty<byte>();
    }
}
