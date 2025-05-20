// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain.Filters
{
    public abstract class FilterBase(int id)
    {
        public int Id { get; } = id;
        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
    }
}
