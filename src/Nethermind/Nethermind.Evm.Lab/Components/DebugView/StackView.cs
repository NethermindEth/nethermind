// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Data;
using DebuggerStateEvents;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Int256;
using Terminal.Gui;

namespace Nethermind.Evm.Lab.Components.DebugView;
internal class StackView : IComponent<(byte[] memory, int height, bool isNewState)>, IDisposable
{
    bool isCached = false;
    private FrameView? container = null;
    private TableView? stackView = null;
    private HexView? rawStackView = null;
    private TabView? viewsAggregator = null;
    private MemoryStream? memoryStream = null;
    private (Button Push, Button Pop) Actions;
    private ContextMenu contextMenu = new ContextMenu();
    private Point mousePosition;

    public void Dispose()
    {
        container?.Dispose();
        stackView?.Dispose();
        viewsAggregator?.Dispose();
        rawStackView?.Dispose();
    }
    public event Action<ActionsBase> EventRequested;
    public event Action<long, byte> ByteEdited;
    public event Action<int> StackHeightChangeRequest;
    public (View, Rectangle?) View((byte[] memory, int height, bool isNewState) stack, Rectangle? rect = null)
    {
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


        viewsAggregator ??= new TabView()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(92),
        };

        Actions.Push ??= new Button("Push")
        {
            Y = Pos.Bottom(viewsAggregator),
            X = Pos.X(viewsAggregator),
            Height = Dim.Percent(7),
        };


        Actions.Pop ??= new Button("Pop")
        {
            X = Pos.Right(Actions.Push),
            Y = Pos.Bottom(viewsAggregator),
            Height = Dim.Percent(7),
        };


        AddClassicalStackView(stack.memory, stack.height);
        AddRawStackView(stack.memory, stack.isNewState);

        if (!isCached)
        {
            Application.RootMouseEvent += Application_RootMouseEvent;

            var normalStackTab = new TabView.Tab("Stack", stackView);
            var rawStackTab = new TabView.Tab("Stack Memory", rawStackView);

            Actions.Push.Clicked += () => StackHeightChangeRequest?.Invoke(1);
            Actions.Pop.Clicked += () => StackHeightChangeRequest?.Invoke(-1);

            viewsAggregator.AddTab(rawStackTab, false);
            viewsAggregator.AddTab(normalStackTab, true);
            container.Add(viewsAggregator, Actions.Pop, Actions.Push);
        }
        isCached = true;
        return (container, frameBoundaries);
    }

    private void AddRawStackView(byte[] state, bool isNewState)
    {
        rawStackView ??= new HexView()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        memoryStream?.Dispose();
        memoryStream = new MemoryStream(state);
        rawStackView.Source = memoryStream;

        if (isNewState)
        {
            rawStackView.DiscardEdits();
        }

        if (!isCached)
        {
            rawStackView.Edited += (e) =>
            {
                ByteEdited?.Invoke(e.Key, e.Value);
            };
        }
    }

    private void AddClassicalStackView(byte[] stack, int stackhead)
    {
        var Uint256Stack = stack
            ?.Take(32 * stackhead).Chunk(32)
             .Select(chunk => new UInt256(chunk, true))
             .Reverse();

        stackView ??= new TableView()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        var dataTable = new DataTable();
        dataTable.Columns.Add("Index");
        dataTable.Columns.Add("Value");

        int stringLen = 42;
        var cleanedUpDataSource = Uint256Stack.Select(entry =>
        {

            string entryStr = entry.ToHexString(false);
            if (entryStr.Length < stringLen)
            {
                entryStr = entryStr.PadLeft(stringLen - entryStr.Length, '0');
            }
            else
            {
                entryStr = entryStr.Substring(entryStr.Length - stringLen);
            }
            return entryStr;
        }).ToList();

        stackView.MouseClick += (e) =>
        {
            var cell = stackView.ScreenToCell(e.MouseEvent.X, e.MouseEvent.Y);

            if (cell is not null && cell.Value.X == 1)
            {
                int pc = stackhead - Int32.Parse((string)stackView.Table.Rows[cell.Value.Y]["Index"]);
                if (e.MouseEvent.Flags == contextMenu.MouseFlags)
                {
                    ShowContextMenu(stack, pc - 1, mousePosition.X, mousePosition.Y);
                    e.Handled = true;
                }
            }

        };

        int index = 0;
        foreach (var value in cleanedUpDataSource)
        {
            dataTable.Rows.Add(index++, value);
        }
        stackView.Table = dataTable;
    }

    private void ShowContextMenu(byte[] arena, int pc, int x, int y)
    {
        Dialog CreateConditionView()
        {
            var submit = new Button("Submit");
            var cancel = new Button("Cancel");
            Dialog container = new Dialog("Stack Item Edit View", 50, 5, submit, cancel)
            {
                X = x,
                Y = y,
            };

            TextField itemView = new TextField()
            {
                Height = 3,
                Width = 48
            };

            Span<byte> item = arena.Slice(32 * pc, 32);
            itemView.Text = item.ToHexString();

            submit.Clicked += () =>
            {
                try
                {
                    byte[] bytes = Bytes.FromHexString(((string)itemView.Text));
                    EventRequested?.Invoke(new UpdateStack(pc, bytes, false));
                }
                catch (Exception ex)
                {
                    MainView.ShowError(ex.Message);
                }
                finally
                {
                    Application.RequestStop();
                }
            };

            cancel.Clicked += () =>
            {
                Application.RequestStop();
            };

            container.Add(itemView);
            return container;
        }


        contextMenu = new ContextMenu(x, y,
            new MenuBarItem(new MenuItem[] {
                new MenuItem ("_Pop", string.Empty, () => EventRequested?.Invoke(new UpdateStack(pc, null, true))),
                new MenuItem ("_Edit", string.Empty, () =>  Application.Run(CreateConditionView()))
            })
        )
        { ForceMinimumPosToZero = true, UseSubMenusSingleFrame = true };


        contextMenu.Show();
    }

    void Application_RootMouseEvent(MouseEvent me)
    {
        mousePosition = new Point(me.X, me.Y);
    }
}
