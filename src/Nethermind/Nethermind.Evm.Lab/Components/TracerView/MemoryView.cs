// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.IdentityModel.Tokens;
using Nethermind.Evm.Lab.Interfaces;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.TracerView;
internal class MemoryView : IComponent<(IEnumerable<byte> ram, bool isDebugger)>
{
    bool isCached = false;
    private FrameView? container = null;
    private HexView? memoryView = null;

    public void Dispose()
    {
        container?.Dispose();
        memoryView?.Dispose();
    }

    public event Action<long, byte> ByteEdited;

    public (View, Rectangle?) View((IEnumerable<byte> ram, bool isDebugger) state, Rectangle? rect = null)
    {
        bool isEmpty = state.ram.IsNullOrEmpty();
        var streamFromBuffer = new MemoryStream(state.ram.ToArray());

        var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? 50,
                Height: rect?.Height ?? 10
            );
        container ??= new FrameView("MemoryState")
        {
            X = frameBoundaries.X,
            Y = frameBoundaries.Y,
            Width = frameBoundaries.Width,
            Height = frameBoundaries.Height,
        };


        memoryView = isEmpty ? new HexView()
        {
            Width = Dim.Fill(2),
            Height = Dim.Fill(2),
        } : memoryView;

        memoryView.Enabled = state.isDebugger;
        memoryView.Clear();
        memoryView.Source = streamFromBuffer;


        if (!isCached || isEmpty)
        {
            memoryView.Edited += (e) =>
            {
                ByteEdited?.Invoke(e.Key, e.Value);
            };
            container.Add(memoryView);
        }
        isCached = true;
        return (container, frameBoundaries);
    }
}
