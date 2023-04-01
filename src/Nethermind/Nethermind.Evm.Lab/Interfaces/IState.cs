// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lab.Interfaces;
internal interface IState<T> where T : IState<T>, new()
{
    IState<T> Initialize(T seed) => new T();

    T GetState() => (T)this;
}
