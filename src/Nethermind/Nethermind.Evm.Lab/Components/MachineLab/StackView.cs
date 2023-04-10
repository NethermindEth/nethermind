// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Lab.Interfaces;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Componants;
internal class StackView : IComponent<MachineState>
{
    bool isCached = false;
    private FrameView? container = null;
    private ListView? stackView = null;
    public (View, Rectangle?) View(IState<MachineState> state, Rectangle? rect = null)
    {
        var innerState = state.GetState();
        var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? 50,
                Height: rect?.Height ?? 10
            );
        container ??= new FrameView("StackState")
        {
            X = frameBoundaries.X,
            Y = frameBoundaries.Y,
            Width = frameBoundaries.Width,
            Height = frameBoundaries.Height,
        };

        stackView ??= new ListView()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2),
        };

        var cleanedUpDataSource = innerState.Current.Stack.Select(entry =>
        {
            int firstNonZeroBitIdx = 0;
            while (firstNonZeroBitIdx < entry.Length && entry[firstNonZeroBitIdx] == '0') firstNonZeroBitIdx++;
            int length = Math.Max(55, entry.Length - firstNonZeroBitIdx);
            return entry[^length..];
        }).ToList();
        stackView.SetSource(cleanedUpDataSource);

        if (!isCached)
            container.Add(stackView);
        isCached = true;
        return (container, frameBoundaries);
    }
}
