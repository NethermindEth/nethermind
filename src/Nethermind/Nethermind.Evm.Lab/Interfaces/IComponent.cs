// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Terminal.Gui;

namespace Nethermind.Evm.Lab.Interfaces;
public record struct Rectangle(Pos? X, Pos? Y, Dim? Width, Dim? Height);

public interface IComponentObject : IDisposable { }
internal interface IComponent<T> : IComponentObject
{
    (View, Rectangle?) View(T _, Rectangle? rect = null);
    T Update(T state, ActionsBase action) => state;
}
internal interface IComponent : IComponentObject
{
    (View, Rectangle?) View(Rectangle? rect = null);
    void Update(ActionsBase action) { }
}

