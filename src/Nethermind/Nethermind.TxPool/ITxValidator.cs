// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.TxPool
{
    public interface ITxValidator
    {
        bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec);
    }
}
