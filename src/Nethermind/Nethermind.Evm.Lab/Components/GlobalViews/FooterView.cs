// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Lab.Interfaces;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.GlobalViews;
internal class FooterView : IComponent<MachineState>
{
    public (View, Rectangle?) View(IState<MachineState> _, Rectangle? rect = null)
    {
        throw new NotImplementedException();
    }
}
