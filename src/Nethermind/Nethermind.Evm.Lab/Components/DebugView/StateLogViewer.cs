// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Data;
using System.Text.Json;
using DebuggerStateEvents;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Tracing.Debugger;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.DebugView;
internal class FooterView : IComponent<EvmState>, IDisposable
{
    private FrameView _frameView;
    private Button _logsOpener;
    private bool isCached;
    private EvmState _capturedState;
    public void Dispose()
    {
        _logsOpener?.Dispose();
        _frameView?.Dispose();
    }

    public (View, Rectangle?) View(EvmState state, Rectangle? rect = null)
    {
        _capturedState = state;

        var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? 50,
                Height: rect?.Height ?? 10
            );

        _frameView ??= new FrameView("State Logs")
        {
            X = frameBoundaries.X,
            Y = frameBoundaries.Y,
            Width = frameBoundaries.Width,
            Height = frameBoundaries.Height,
        };

        _logsOpener ??= new Button("show logs")
        {
            X = Pos.AnchorEnd(15),
        };
        _logsOpener.Enabled = _capturedState is not null;

        if (!isCached)
        {
            _logsOpener.Clicked += () =>
            {
                var cancel = new Button("Cancel");
                cancel.Clicked += () =>
                {
                    Application.RequestStop();
                };

                Dialog dialog = new Dialog("State logs", 50, 43, cancel);
                TextView text = new TextView()
                {
                    Width = Dim.Fill(),
                    Height = Dim.Percent(95),
                };
                text.KeyPress += (e) => e.Handled = true;

                text.Text = JsonSerializer.Serialize(_capturedState, new JsonSerializerOptions
                {
                    WriteIndented = true,
                });
                dialog.Add(text);

                Application.Run(dialog);
            };

            _frameView.Add(_logsOpener);
        }
        isCached = true;

        return (_frameView, null);
    }
}
