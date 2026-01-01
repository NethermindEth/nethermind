// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Xdc;

internal class PenaltyHandler : IPenaltyHandler
{
    public Address[] HandlePenalties(ulong number, Hash256 currentHash, Address[] candidates)
    {
        //Mock implementation for now
        return [];
    }
}
