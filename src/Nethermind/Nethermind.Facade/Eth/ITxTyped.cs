// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Facade.Eth;

public interface ITxTyped
{
    static abstract TxType TxType { get; }
}
