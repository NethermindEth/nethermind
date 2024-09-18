// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;
using NUnit.Framework;

namespace Ethereum.Test.Base;

public class TextContextLogger : InterfaceLogger
{
    private TextContextLogger() { }

    public static TextContextLogger Instance { get; } = new TextContextLogger();

    public void Info(string text) => WriteEntry(text);

    public void Warn(string text) => WriteEntry(text);

    public void Debug(string text) => WriteEntry(text);

    public void Trace(string text) => WriteEntry(text);

    public void Error(string text, Exception ex = null) => WriteEntry(text + " " + ex);

    private static void WriteEntry(string text) => TestContext.WriteLine(text);

    public bool IsInfo => true;
    public bool IsWarn => true;
    public bool IsDebug => true;
    public bool IsTrace => true;
    public bool IsError => true;
}
