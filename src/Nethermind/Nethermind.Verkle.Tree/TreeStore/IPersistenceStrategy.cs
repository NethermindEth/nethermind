// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

namespace Nethermind.Verkle.Tree.TreeStore;

public interface IPersistenceStrategy
{
    public static abstract bool IsUsingCache { get; }
    public static abstract int CacheSize { get; }
}

public readonly struct VerkleSyncCache : IPersistenceStrategy
{
    public static bool IsUsingCache => true;
    public static int CacheSize => 128;
}

public readonly struct ReorgCache : IPersistenceStrategy
{
    public static bool IsUsingCache => true;
    public static int CacheSize => 64;
}

public readonly struct PersistEveryBlock: IPersistenceStrategy
{
    public static bool IsUsingCache => false;
    public static int CacheSize => 0;
}

