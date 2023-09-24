// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Data;
using System.Security.Cryptography.Xml;
using System.Xml.Linq;
using DebuggerStateEvents;
using MachineStateEvents;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Lab.Components.Differ;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Lab.Parser;
using Nethermind.Evm.Tracing.Debugger;
using Nethermind.Specs;
using NSubstitute;
using Terminal.Gui;
using Terminal.Gui.Trees;
using static Microsoft.FSharp.Core.ByRefKinds;

namespace Nethermind.Evm.Lab.Components.DebugView;
internal class StacktraceView : IComponent<DebugTracer>
{
    private class CallTrace : TreeNode
    {
        public List<StackTrace> Calltrace = new();
        public override IList<ITreeNode> Children => Calltrace.Cast<ITreeNode>().ToList();
    }

    private class SingleTrace : TreeNode
    {
        public (int, int) returnStackEntry { get; set; }

        public override string Text => $"Section: {returnStackEntry.Item1} Offset: {returnStackEntry.Item2}";
    }
    private class StackTrace : TreeNode
    {
        public int Depth { get; set; }
        public Address Address { get; set; }
        public List<SingleTrace> Callstack { get; set; }
        public override IList<ITreeNode> Children => Callstack.Cast<ITreeNode>().ToList();
        public override string Text
        {
            get => $"{Depth}: {Address}";
            set => throw new System.Diagnostics.UnreachableException();
        }
    }


    private bool isCached = false;
    private FrameView? container = null;
    private TreeView? stackview = null;
    private CallTrace? _callTrace = new()
    {
        Text = "Current Call Stack"
    };

    public void Dispose()
    {
        container?.Dispose();
        stackview?.Dispose();
    }

    public (View, Rectangle?) View(DebugTracer tracer, Rectangle? rect = null)
    {
        if (tracer.CurrentState is null)
        {
            _callTrace.Children.Clear();
        }

        var frameBoundaries = new Rectangle(
                X: rect?.X ?? 0,
                Y: rect?.Y ?? 0,
                Width: rect?.Width ?? 50,
                Height: rect?.Height ?? 10
            );

        container ??= new FrameView("CallStackTrace")
        {
            X = frameBoundaries.X,
            Y = frameBoundaries.Y,
            Width = frameBoundaries.Width,
            Height = frameBoundaries.Height,
        };

        stackview ??= new TreeView()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };



        int? depth = tracer?.CurrentState?.Env.CallDepth;
        Address? codeinfoSrc = tracer?.CurrentState?.Env.CodeSource ?? tracer?.CurrentState?.Env.ExecutingAccount;
        var stacktrace = tracer?.CurrentState?.ReturnStack?[..(tracer?.CurrentState?.ReturnStackHead ?? 0)].ToList() ?? new List<int>();
        stacktrace.Add(0);


        if (tracer?.CurrentState is not null)
        {

            _callTrace.Calltrace = _callTrace.Calltrace.Take(tracer.CurrentState?.Env.CallDepth ?? 0).ToList();

            bool found = false;
            foreach (var item in _callTrace.Calltrace)
            {
                if (item.Depth == depth && item.Address == codeinfoSrc)
                {
                    item.Callstack = stacktrace.Select(traceEntry => new SingleTrace
                    {
                        returnStackEntry = (0, traceEntry)
                    }).ToList();
                    found = true; break;
                }
            }

            if (!found)
            {
                _callTrace.Calltrace.Add(
                    new StackTrace
                    {
                        Depth = depth.Value,
                        Address = codeinfoSrc,
                        Callstack = stacktrace.Select(traceEntry => new SingleTrace
                        {
                            returnStackEntry = (0, traceEntry)
                        }).ToList()
                    }
                );
            }
        }

        stackview.RebuildTree();
        stackview.ExpandAll();

        if (!isCached)
        {
            stackview.AddObject(_callTrace);
            container.Add(stackview);
        }

        isCached = true;
        return (container, frameBoundaries);
    }
}
