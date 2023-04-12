// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using MachineState.Actions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Specs.Forks;
using NStack;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.MachineLab;
internal class NumberInputField : TextField
{
    public override TextChangingEventArgs OnTextChanging(ustring newText)
    {
        if (newText.Where(c => Char.IsAsciiLetter((char)c)).Any())
            return new TextChangingEventArgs(this.Text);
        return base.OnTextChanging(newText);
    }
}
