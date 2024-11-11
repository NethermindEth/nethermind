// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Taiko;

public interface IL1OriginStore
{
    L1Origin? ReadL1Origin(UInt256 blockId);
    void WriteL1Origin(UInt256 blockId, L1Origin l1Origin);

    UInt256? ReadHeadL1Origin();
    void WriteHeadL1Origin(UInt256 blockId);
}
