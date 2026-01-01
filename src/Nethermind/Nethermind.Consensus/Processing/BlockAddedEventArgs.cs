// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Processing;

public class BlockAddedEventArgs(Hash256 blockHash) : EventArgs
{
    public Hash256 BlockHash => blockHash;
}
