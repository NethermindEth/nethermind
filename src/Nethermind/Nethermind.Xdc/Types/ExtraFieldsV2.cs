// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc.Types;

public class ExtraFieldsV2(ulong round, QuorumCert quorumCert)
{
    public ulong Round { get; set; } = round;
    public QuorumCert QuorumCert { get; set; } = quorumCert;

    public byte[] EncodeToBytes()
    {
        var bytes = Rlp.Encode(this).Bytes;

        return [2, .. bytes];
    }
}
