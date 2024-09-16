// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Taiko;

public record L1Origin(UInt256 BlockID, Hash256? L2BlockHash, long L1BlockHeight, Hash256 L1BlockHash);
