// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core2
{
    public interface IClock
    {
        DateTimeOffset Now();
        DateTimeOffset UtcNow();
    }
}
