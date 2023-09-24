// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Lab.Interfaces;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.TracerView;
internal class ReturnView : IComponent<byte[]>
{
    bool isCached = false;
    private FrameView? container = null;
    private HexView? memoryView = null;

    public void Dispose()
    {
        container?.Dispose();
        memoryView?.Dispose();
    }

    public (View, Rectangle?) View(byte[] state, Rectangle? rect = null)
    {
        var streamFromBuffer = new MemoryStream(state);

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
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        memoryView.Source = streamFromBuffer;

        if (!isCached)
            container.Add(memoryView);
        isCached = true;
        return (container, frameBoundaries);
    }
}
