// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

public interface IFlag
{
    virtual static bool IsActive { get; }
}

public struct OffFlag : IFlag
{
    public static bool IsActive => false;
}
public struct OnFlag : IFlag
{
    public static bool IsActive => true;
}
