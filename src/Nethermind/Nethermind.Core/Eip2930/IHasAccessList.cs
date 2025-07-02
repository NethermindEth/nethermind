// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Core.Eip2930;

public interface IHasAccessList
{
    AccessList? GetAccessList(Block block, IReleaseSpec spec);
}
