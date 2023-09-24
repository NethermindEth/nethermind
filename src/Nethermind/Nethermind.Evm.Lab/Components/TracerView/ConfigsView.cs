// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using MachineStateEvents;
using Nethermind.Core.Specs;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Test;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.TracerView;
internal class ConfigsView : IComponent<(IReleaseSpec Spec, long GasAvailable)>
{
    bool isCached = false;
    private FrameView? container = null;
    private ComboBox? forksChoice = null;
    private NumberInputField? gasValueInput = null;
    public event Action<ActionsBase> ConfigsChanged;
    //private CheckBox? ignoreGasCheck = null;

    private List<string> Forks = typeof(ReleaseSpec).Module.GetTypes().Where(type => type.Namespace == typeof(ReleaseSpec).Namespace && type.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() == null).Select(type => type.Name).Append("Custom").ToList();

    public void Dispose()
    {
        container?.Dispose();
        forksChoice?.Dispose();
        gasValueInput?.Dispose();
    }

    public (View, Rectangle?) View((IReleaseSpec Spec, long GasAvailable) state, Rectangle? rect = null)
    {
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

        var label_forkChosen = new Label(state.Spec.Name)
        {
            Width = Dim.Percent(75)
        };
        forksChoice ??= new ComboBox("Fork Selection")
        {
            Y = Pos.Bottom(label_forkChoser),
            Height = Dim.Percent(50),
            Width = Dim.Fill(),
            HideDropdownListOnClick = true
        };
        forksChoice.Add(label_forkChosen);

        var label_gasSetter = new Label("Gas Setting")
        {
            Y = Pos.Bottom(forksChoice),
            Width = Dim.Fill(),
        };
        gasValueInput ??= new NumberInputField(VirtualMachineTestsBase.DefaultBlockGasLimit)
        {
            Y = Pos.Bottom(label_gasSetter),
            Width = Dim.Fill(),
        };
        gasValueInput.Text = state.GasAvailable.ToString();


        if (!isCached)
        {
            forksChoice.CanFocus = false;
            forksChoice.SetSource(Forks);
            forksChoice.SelectedItemChanged += (e) =>
            {
                if (!forksChoice.HasFocus) return;
                var forkName = e.Value.ToString();
                if (forkName != "Custom")
                {
                    var chosenFork = (IReleaseSpec)typeof(Frontier).Module.GetTypes().First(type => type.Name == forkName).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public).GetValue(null);
                    ConfigsChanged?.Invoke(new SetForkChoice(chosenFork));
                }
                else
                {
                    var eipSelectionDialog = new EipSelectionView();
                    eipSelectionDialog.EipSelectionChanged += (msg) => ConfigsChanged?.Invoke(msg);
                    Application.Run((Dialog)eipSelectionDialog.View(state.Spec).Item1);
                }
            };

            gasValueInput.KeyPress += (e) =>
            {
                if (e.KeyEvent.Key is not Key.Enter || !gasValueInput.HasFocus) return;
                if (Int32.TryParse((string)gasValueInput.Text, out int gasValue))
                {
                    ConfigsChanged?.Invoke(new SetGasMode(false, gasValue));
                }
            };
            //ignoreGasCheck.Add(gasValueInput);
            container.Add(label_forkChoser, forksChoice, label_gasSetter, /*ignoreGasCheck*/ gasValueInput);
        }
        isCached = true;
        return (container, frameBoundaries);
    }

    private void EipSelectionDialog_EipSelectionChanged(SetForkChoice obj)
    {
        throw new NotImplementedException();
    }
}
