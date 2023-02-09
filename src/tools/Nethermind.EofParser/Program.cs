// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using Nethermind.Core.Extensions;
using Nethermind.Evm.EOF;
string line;

int idx = 1;
EvmObjectFormat.Logger = new InMemoryLogger
{
    IsTrace = true
};

while ((line = Console.ReadLine()) != null)
{
    HandleLine(idx++, line);
}

void HandleLine(int idx, string line)
{
    if (line.StartsWith("#"))
    {
        return;
    }

    line = new string(line.Where(c => char.IsLetterOrDigit(c)).ToArray());

    var bytecode = Bytes.FromHexString(line);
    try
    {
        var result = EvmObjectFormat.IsValidEof(bytecode, out EofHeader? header);
        if (result)
        {
            var codeSections = string.Join(",", header?.CodeSections.Select(section =>
                {
                    var start = section.Start;
                    var end = section.EndOffset;
                    var code = bytecode[start..end];
                    return code.ToHexString();
                })).ToLower();
            Console.WriteLine($"OK {codeSections}");
        }
        else
        {
            Console.WriteLine($"err: {(String.IsNullOrEmpty(LogInterceptor.Content) ? "Invalid Eof Format" : LogInterceptor.Content)}");
        }
        LogInterceptor.Clear();
    }
    catch (Exception e)
    {
        Console.WriteLine($"err: exception with msg : {e.Message} at line {idx} with stack trace {e.StackTrace}");
    }
}
