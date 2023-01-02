// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm.EOF;

internal static class EvmObjectFormatCodeInfoExtensions
{
    public static bool IsEof(this ICodeInfo codeInfo) =>
        EvmObjectFormat.IsEof(codeInfo.MachineCode);

    public static byte EofVersion(this ICodeInfo codeInfo) =>
        codeInfo is EofCodeInfo eofCodeInfo ? eofCodeInfo.Version : (byte)0;
}
