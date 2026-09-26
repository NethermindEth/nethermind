// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Stateless;

namespace Nethermind.Eez.Execution.Stateless;

/// <summary>One block of a window: its consensus RLP and the witness of the state it executes on.</summary>
public sealed record EezStatelessBlock(byte[] Rlp, Witness Witness);
