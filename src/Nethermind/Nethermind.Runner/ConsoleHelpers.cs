// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Nethermind.Runner;

public static class ConsoleHelpers
{
    private static LineInterceptingTextWriter _interceptingWriter;
    public static event EventHandler<string>? LineWritten;
    public static string[] GetRecentMessages() => _interceptingWriter.GetRecentMessages();

    public static void EnableConsoleColorOutput()
    {
        const int STD_OUTPUT_HANDLE = -11;
        const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 4;

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Capture original out
        TextWriter originalOut = Console.Out;

        // Create our intercepting writer
        _interceptingWriter = new LineInterceptingTextWriter(originalOut);
        _interceptingWriter.LineWritten += (sender, line) =>
        {
            LineWritten?.Invoke(sender, line);
        };

        // Redirect Console.Out
        Console.SetOut(_interceptingWriter);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            return;

        try
        {
            // If using Cmd and not set in registry
            // enable ANSI escape sequences here
            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            GetConsoleMode(handle, out var mode);
            mode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
            SetConsoleMode(handle, mode);
        }
        catch
        {
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}

public sealed class LineInterceptingTextWriter(TextWriter underlyingWriter) : TextWriter
{
    // Event raised every time a full line ending with Environment.NewLine is written
    public event EventHandler<string>? LineWritten;

    // The "real" underlying writer (i.e., the original Console.Out)
    private readonly TextWriter _underlyingWriter = underlyingWriter ?? throw new ArgumentNullException(nameof(underlyingWriter));

    // Buffer used to accumulate written data until we detect a new line
    private readonly StringBuilder _buffer = new();

    // You must override Encoding, even if just forwarding
    public override Encoding Encoding => _underlyingWriter.Encoding;

    private const int MaxMessageBackfill = 100;

    private readonly Lock _messageLock = new();
    private readonly Queue<string> _recentMessages = new(capacity: MaxMessageBackfill);
    private string[]? _messages;

    // Overriding WriteLine(string) is handy for direct calls to Console.WriteLine(...).
    // However, you also want to handle the general case in Write(string).
    public override void WriteLine(string? value)
    {
        Write(value);
        Write(Environment.NewLine);
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        // Append to the buffer
        _buffer.Append(value);

        // Pass the data along to the underlying writer
        _underlyingWriter.Write(value);

        // Check if we can extract lines from the buffer
        CheckForLines();
    }

    public override void Write(char value)
    {
        _buffer.Append(value);
        _underlyingWriter.Write(value);
        CheckForLines();
    }

    public override void Flush()
    {
        base.Flush();
        _underlyingWriter.Flush();
    }

    // Environment.NewLine might be "\r\n" or "\n" depending on platform
    private readonly static string EnvironmentNewLine = Environment.NewLine;

    private void CheckForLines()
    {
        // let's find each occurrence of new line and split it off
        while (true)
        {
            // Find the next index of the new line
            int newLinePos = _buffer.ToString().IndexOf(EnvironmentNewLine, StringComparison.Ordinal);

            // If there's no complete new line, break
            if (newLinePos < 0)
            {
                break;
            }

            // Extract the line up to the new line
            string line = _buffer.ToString(0, newLinePos);

            // Remove that portion (including the new line) from the buffer
            _buffer.Remove(0, newLinePos + EnvironmentNewLine.Length);

            // Raise the event
            OnLineWritten(line);
        }
    }

    public string[] GetRecentMessages()
    {
        string[]? messages = _messages;
        if (messages is null)
        {
            lock (_messageLock)
            {
                messages = (_messages ??= [.. _recentMessages]);
            }
        }

        return messages;
    }

    private void OnLineWritten(string line)
    {
        lock (_messageLock)
        {
            _messages = null;
            if (_recentMessages.Count >= MaxMessageBackfill)
            {
                _recentMessages.Dequeue();
            }
            _recentMessages.Enqueue(line);
        }
        // Raise the event, if subscribed
        LineWritten?.Invoke(this, line);
    }
}
