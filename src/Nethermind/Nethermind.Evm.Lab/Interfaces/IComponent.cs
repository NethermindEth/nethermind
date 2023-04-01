// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Terminal.Gui;

namespace Nethermind.Evm.Lab.Interfaces;
public record struct Rectangle(Pos? X, Pos? Y, Dim? Width, Dim? Height);
internal interface IComponent<T> where T : IState<T>, new()
{
    (View, Rectangle) View(IState<T> _, Rectangle? rect = null);
    IState<T> Update(IState<T> state, ActionsBase action) => state;
}
