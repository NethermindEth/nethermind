// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Memory;

/// <summary>
/// Wrapper around malloc apis
/// </summary>
public class MallocHelper
{
    [DllImport("libc")]
    private static extern int mallopt(int opts, int value);

    [DllImport("libc")]
    private static extern int malloc_trim(UIntPtr trailingSpace);

    private static MallocHelper? _instance;
    public static MallocHelper Instance => _instance ??= new MallocHelper();

    public virtual bool MallOpt(Option option, int value)
    {
        return mallopt((int)option, value) == 1;
    }

    public virtual bool MallocTrim(uint trailingSpace)
    {
        return malloc_trim(trailingSpace) == 1;
    }


    public enum Option: int
    {
        M_MMAP_THRESHOLD = -3
    }
}
