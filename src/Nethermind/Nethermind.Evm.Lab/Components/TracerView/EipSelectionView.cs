// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.RegularExpressions;
using MachineStateEvents;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Specs;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.TracerView;
internal class EipSelectionView : IComponent<IReleaseSpec>
{
    bool isCached = false;
    private Dialog? container = null;
    private CheckBox[]? EipCheckBoxes = null;
    private string[] eipNumbers;

    public event Action<SetForkChoice> EipSelectionChanged;

    Dictionary<string, bool> _currentSpecMap = new Dictionary<string, bool>();
    private Dictionary<string /*actually int*/, bool> CurrentSelectedEips(IReleaseSpec spec)
    {
        foreach (var eipNumber in eipNumbers)
        {
            _currentSpecMap[eipNumber] = (bool)typeof(ReleaseSpec).GetProperty($"IsEip{eipNumber}Enabled").GetValue(spec, null);
        }
        return _currentSpecMap;
    }
    private ReleaseSpec _releaseSpec
    {
        get
        {
            var releaseSpec = new ReleaseSpec();
            foreach (var (eipNumber, isSet) in _currentSpecMap)
            {
                typeof(ReleaseSpec).GetProperty($"IsEip{eipNumber}Enabled").SetValue(releaseSpec, isSet);
            }
            return releaseSpec;
        }
    }

    public (View, Rectangle?) View(IReleaseSpec state, Rectangle? rect = null)
    {
        var frameBoundaries = new Rectangle(
                X: rect?.X ?? Pos.Center(),
                Y: rect?.Y ?? Pos.Center(),
                Width: rect?.Width ?? Dim.Percent(20),
                Height: rect?.Height ?? Dim.Percent(75)
            );

        if (!isCached)
        {
            eipNumbers ??= typeof(ReleaseSpec).GetProperties().Where(prop => prop.PropertyType == typeof(bool) && Regex.IsMatch(prop.Name, "IsEip(\\d+)Enabled"))
                    .Select(prop => prop.Name[5..^7]).ToArray();

            var currentSelectedEips = CurrentSelectedEips(state);

            CheckBox? previousCheckbox = null;
            int heightAcc = 0;
            EipCheckBoxes ??= eipNumbers.Select(eipNumber =>
            {
                var checkbox = new CheckBox($"Activate Eip {eipNumber}", currentSelectedEips[eipNumber])
                {
                    Y = previousCheckbox is not null ? Pos.Bottom(previousCheckbox) : 0,
                    Width = Dim.Fill(),
                    Height = 2,
                    Border = new Border()
                };

                checkbox.Toggled += (e) =>
                {
                    _currentSpecMap[eipNumber] = checkbox.Checked;
                };
                previousCheckbox = checkbox;
                heightAcc += 2;
                return checkbox;
            }).ToArray();

            var scrollView = new ScrollView
            {
                X = 2,
                Y = 2,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 2,
                ContentSize = new Size(previousCheckbox.Bounds.Width, heightAcc),
                //ContentOffset = new Point (0, 0),
                ShowVerticalScrollIndicator = true,
                ShowHorizontalScrollIndicator = true,
            };
            scrollView.Add(EipCheckBoxes);


            var submit = new Button("Submit");
            var cancel = new Button("Cancel");
            container ??= new Dialog("Eip Selection Panel", 60, 7, submit, cancel)
            {
                X = frameBoundaries.X,
                Y = frameBoundaries.Y,
                Width = frameBoundaries.Width,
                Height = frameBoundaries.Height,
            };

            container.Add(scrollView);

            submit.Clicked += () =>
            {
                EipSelectionChanged?.Invoke(new SetForkChoice(_releaseSpec));
                Application.RequestStop();
            };

            cancel.Clicked += () =>
            {
                Application.RequestStop();
            };
        }
        isCached = true;

        return (container, frameBoundaries);
    }

    public void Dispose()
    {
        container?.Dispose();
        EipCheckBoxes.ForEach(x => x.Dispose());
    }
}
