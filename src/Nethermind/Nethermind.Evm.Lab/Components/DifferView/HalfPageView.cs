// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Lab.Components.TracerView;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Lab.Parser;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.Differ;
internal class HalfPageView : IComponent<TraceState>
{
    bool isCached = false;
    private FrameView? container = null;
    private MachineDataView? processorView = null;
    private EntriesView? entriesView = null;
    private StackView? stackView = null;
    private MemoryView? memoView = null;
    private int HalfIndex = 0;

    private string TitleName = string.Empty;
    public HalfPageView(string titleName, int halfIndex)
    {
        processorView ??= new MachineDataView();
        stackView ??= new StackView();
        memoView ??= new MemoryView();
        TitleName = titleName;
        HalfIndex = halfIndex;
    }

    public void Dispose()
    {
        processorView?.Dispose();
        entriesView?.Dispose();
        stackView?.Dispose();
        memoView?.Dispose();
    }

    public (View, Rectangle?) View(TraceState state, Rectangle? rect = null)
    {
        var innerState = (HalfIndex == 0 ? state.Traces.target : state.Traces.subject).State;

        var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? 50,
                Height: rect?.Height ?? 10
            );

        container ??= new FrameView(TitleName)
        {
            X = frameBoundaries.X,
            Y = frameBoundaries.Y,
            Width = frameBoundaries.Width,
            Height = frameBoundaries.Height,
        };

        entriesView ??= new EntriesView(state.DifferenceStartIndex);


        var (cpu_view, cpu_rect) = processorView.View(innerState.Current, new Rectangle(0, 0, Dim.Fill(), Dim.Percent(30))); // h:10 w:30
        var (entries_view, entries_rect) = entriesView.View(innerState, cpu_rect.Value with // h:50 w:30
        {
            Y = Pos.Bottom(cpu_view),
            Height = Dim.Percent(30)
        });
        var (stack_view, stack_rect) = stackView.View(innerState.Current.Stack.Select(entry => Bytes.FromHexString(entry)).ToArray(), entries_rect.Value with // h:50 w:30
        {
            Y = Pos.Bottom(entries_view),
            Height = Dim.Percent(25)
        });

        var memoryState = Core.Extensions.Bytes.FromHexString(String.Join(string.Empty, innerState.Current.Memory.Select(row => row.Replace("0x", String.Empty))));
        var (memory_view, memory_rect) = memoView.View((memoryState, false), stack_rect.Value with // h:50 w:30
        {
            Y = Pos.Bottom(stack_view),
            Height = Dim.Percent(25)
        });

        if (!isCached)
        {
            container.Add(cpu_view, entries_view, stack_view, memory_view);
            isCached = true;
        }

        return (container, frameBoundaries);
    }
}
