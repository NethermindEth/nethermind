// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain.Data
{
    public interface ILocalDataSource<out T>
    {
        T Data { get; }
        event EventHandler Changed;
    }
}
