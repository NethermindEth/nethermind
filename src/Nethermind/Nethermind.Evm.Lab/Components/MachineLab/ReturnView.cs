// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Lab.Interfaces;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Componants;
internal class ReturnView : IComponent<MachineState>
{
    bool isCached = false;
    private FrameView? container = null;
    private HexView? memoryView = null;
    public (View, Rectangle?) View(IState<MachineState> state, Rectangle? rect = null)
    {
        var innerState = state.GetState();

        var streamFromBuffer = new MemoryStream(innerState.ReturnValue);

        var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? 50,
                Height: rect?.Height ?? 10
            );

        container ??= new FrameView("ReturnState")
        {
            X = frameBoundaries.X,
            Y = frameBoundaries.Y,
            Width = frameBoundaries.Width,
            Height = frameBoundaries.Height,
        };

        memoryView ??= new HexView()
        {
            Width = Dim.Fill(2),
            Height = Dim.Fill(2),
        };
        memoryView.Source = streamFromBuffer;

        if (!isCached)
            container.Add(memoryView);
        isCached = true;
        return (container, frameBoundaries);
    }
}
