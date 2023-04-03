// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using MachineState.Actions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Specs.Forks;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.MachineLab;
internal class ConfigsView : IComponent<MachineState>
{
    bool isCached = false;
    private FrameView? container = null;
    private ComboBox? forksChoice = null;
    private TextField? gasValueInput = null;
    //private CheckBox? ignoreGasCheck = null;

    private List<string> Forks = typeof(Shanghai).Module.GetTypes().Where(type => type.Namespace == typeof(Shanghai).Namespace && type.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() == null).Select(type => type.Name).Append("Custom").ToList();

    public (View, Rectangle?) View(IState<MachineState> state, Rectangle? rect = null)
    {
        var innerState = state.GetState();
        var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? 50,
                Height: rect?.Height ?? 10
            );
        container ??= new FrameView("ConfigPanel")
        {
            X = frameBoundaries.X,
            Y = frameBoundaries.Y,
            Width = frameBoundaries.Width,
            Height = frameBoundaries.Height,
        };

        var label_forkChoser = new Label("Fork Choise")
        {
            Width = Dim.Fill(),
        };

        var label_forkChosen = new Label(innerState.SelectedFork.Name)
        {
            Width = Dim.Percent(75)
        };
        forksChoice ??= new ComboBox("Fork Selection")
        {
            Y = Pos.Bottom(label_forkChoser),
            Height = Dim.Percent(25),
            Width = Dim.Fill(),
            HideDropdownListOnClick = true
        };
        forksChoice.Add(label_forkChosen);

        var label_gasSetter = new Label("Gas Setting")
        {
            Y = Pos.Bottom(forksChoice),
            Width = Dim.Fill(),
        };
        gasValueInput ??= new TextField()
        {
            Y = Pos.Bottom(label_gasSetter),
            Width = Dim.Fill(),
        };
        gasValueInput.Text = innerState.AvailableGas.ToString();


        if (!isCached)
        {
            forksChoice.CanFocus = false;
            forksChoice.SetSource(Forks);
            forksChoice.SelectedItemChanged += (e) =>
            {
                var forkName = e.Value.ToString();
                if (forkName != "Custom")
                {
                    var chosenFork = (IReleaseSpec)typeof(Frontier).Module.GetTypes().First(type => type.Name == forkName).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public).GetValue(null);
                    EventsSink.EnqueueEvent(new SetForkChoice(chosenFork));
                }
                else
                {
                    var eipSelectionDialog = new EipSelectionView().View(state).Item1;
                    Application.Run((Dialog)eipSelectionDialog);
                }
            };

            gasValueInput.KeyPress += (e) =>
            {
                if (e.KeyEvent.Key is not Key.Enter) return;
                if (Int32.TryParse((string)gasValueInput.Text, out int gasValue))
                {
                    EventsSink.EnqueueEvent(new SetGasMode(false, gasValue));
                }
            };
            //ignoreGasCheck.Add(gasValueInput);
            container.Add(label_forkChoser, forksChoice, label_gasSetter, /*ignoreGasCheck*/ gasValueInput);
        }
        isCached = true;
        return (container, frameBoundaries);
    }
}
