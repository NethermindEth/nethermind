// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc.Types;

using Round = ulong;

public class ExtraFieldsV2
{
    public ExtraFieldsV2(ulong round, QuorumCert quorumCert)
    {
        Round = round;
        QuorumCert = quorumCert;
    }

    public Round Round { get; set; }
    public QuorumCert QuorumCert { get; set; }

    public byte[] EncodeToBytes()
    {
        var bytes = Rlp.Encode(this).Bytes;

        return [2, .. bytes];
    }
}
