// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Taiko;

public interface IL1OriginStore
{
    L1Origin? ReadL1Origin(UInt256 blockid);
    void WriteL1Origin(UInt256 blockid, L1Origin l1Origin);

    UInt256? ReadHeadL1Origin();
    void WriteHeadL1Origin(UInt256 blockid);
}
