// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Collections;

public interface IToArrayCollection<T> : IReadOnlyCollection<T>
{
    T[] ToArray();
}
