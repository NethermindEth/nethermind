// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL;

/// <summary>
/// Provides 
/// </summary>
internal static class IlAnalyzer
{
    /// <summary>
    /// Starts the analyzing in a background task and outputs the value in the <paramref name="codeInfo"/>.
    /// </summary>
    /// <param name="machineCode">The code to analyze.</param>
    /// <param name="codeInfo">The destination output.</param>
    public static void StartAnalysis(byte[] machineCode, CodeInfo codeInfo)
    {
        Task.Run(() =>
        {
            IlInfo info = Analysis(machineCode);
            codeInfo.SetIlInfo(info);
        });
    }

    /// <summary>
    /// For now, return null always to default to EVM.
    /// </summary>
    private static IlInfo Analysis(byte[] machineCode)
    {
        // TODO: implement actual analysis.
        return IlInfo.NoIlEVM;
    }

    /// <summary>
    /// How many execution a <see cref="CodeInfo"/> should perform before trying to get its opcodes optimized.
    /// </summary>
    public const int IlAnalyzerThreshold = 23;
}
