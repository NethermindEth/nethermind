// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DebuggerStateEvents;
using Nethermind.Core.Collections;
using Nethermind.Evm.Lab.Interfaces;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.DebugView;
internal class MediaLikeView : IComponent<(bool isThreadOn, bool canReadState)>
{
    bool isCached = false;
    private FrameView? container = null;
    private Button[] buttons;
    private Button StartResetButtom;
    public MediaLikeView(Action<ActionsBase> actionHandler = null)
    {
        ActionRequested += actionHandler;
    }
    public void Dispose()
    {
        container?.Dispose();
    }

    public event Action<ActionsBase> ActionRequested;

    public (View, Rectangle?) View((bool isThreadOn, bool canReadState) state, Rectangle? rect = null)
    {
        var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? Dim.Fill(),
                Height: rect?.Height ?? Dim.Percent(10)
            );
        container ??= new FrameView("Program Controls")
        {
            X = frameBoundaries.X,
            Y = frameBoundaries.Y,
            Width = frameBoundaries.Width,
            Height = frameBoundaries.Height,
        };


        if (!isCached)
        {
            string[] labels = new[] { "next", "step", "abort" };
            ActionsBase[] events = new ActionsBase[] { new MoveNext(true), new MoveNext(false), new Abort() };
            Button? previousButton = null;
            buttons = labels.Zip(events).Select(pair =>
            {
                (string label, ActionsBase msg) = pair;
                var actionButton = new Button(label)
                {
                    Width = Dim.Percent(25),
                    X = previousButton is null ? 0 : Pos.Right(previousButton),
                };
                actionButton.Clicked += () =>
                {
                    ActionRequested?.Invoke(msg);
                };
                previousButton = actionButton;
                return actionButton;
            }).ToArray();
            container.Add(buttons);
        }

        if (state.isThreadOn)
        {
            container.Remove(StartResetButtom);
            StartResetButtom = new Button("reset")
            {
                X = Pos.Right(buttons[2])
            };
            StartResetButtom.Clicked += () => ActionRequested?.Invoke(new Reset());
            container.Add(StartResetButtom);

        }
        else
        {
            container.Remove(StartResetButtom);
            StartResetButtom = new Button("start")
            {
                X = Pos.Right(buttons[2])
            };
            StartResetButtom.Clicked += () => ActionRequested?.Invoke(new Start());
            container.Add(StartResetButtom);
        }

        buttons.ForEach(btn => btn.Enabled = state.canReadState);

        isCached = true;
        return (container, frameBoundaries);
    }
}
