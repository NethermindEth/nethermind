// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;

namespace Nethermind.Xdc;
internal interface IXdcSealer : ISealer
{
    bool CanSeal(ulong round, XdcBlockHeader parentHash);
}
