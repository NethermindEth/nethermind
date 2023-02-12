// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Reflection;

namespace Nethermind.Db.Rocks.Config;

public class PerTableDbConfig
{
    private readonly string _tableName;
    private readonly IDbConfig _dbConfig;
    private readonly RocksDbSettings _settings;

    public PerTableDbConfig(IDbConfig dbConfig, RocksDbSettings rocksDbSettings)
    {
        _dbConfig = dbConfig;
        _settings = rocksDbSettings;
        _tableName = _settings.DbName;
    }

    public bool CacheIndexAndFilterBlocks => _settings.CacheIndexAndFilterBlocks.HasValue
            ? _settings.CacheIndexAndFilterBlocks.Value
            : ReadConfig<bool>(nameof(_dbConfig.CacheIndexAndFilterBlocks));

    public ulong BlockCacheSize => _settings.BlockCacheSize.HasValue
            ? _settings.BlockCacheSize.Value
            : ReadConfig<ulong>(nameof(_dbConfig.BlockCacheSize));

    public ulong WriteBufferSize => _settings.WriteBufferSize.HasValue
            ? _settings.WriteBufferSize.Value
            : ReadConfig<ulong>(nameof(_dbConfig.WriteBufferSize));

    public ulong WriteBufferNumber => _settings.WriteBufferNumber.HasValue
            ? _settings.WriteBufferNumber.Value
            : ReadConfig<uint>(nameof(_dbConfig.WriteBufferNumber));

    private T? ReadConfig<T>(string propertyName)
    {
        return ReadConfig<T>(_dbConfig, propertyName, _tableName);
    }

    private static T? ReadConfig<T>(IDbConfig dbConfig, string propertyName, string tableName)
    {
        string prefixed = string.Concat(tableName.StartsWith("State") ? string.Empty : string.Concat(tableName, "Db"),
            propertyName);
        try
        {
            Type type = dbConfig.GetType();
            PropertyInfo? propertyInfo = type.GetProperty(prefixed, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            // if no custom db property default to generic one
            propertyInfo ??= type.GetProperty(propertyName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            return (T?)propertyInfo?.GetValue(dbConfig);
        }
        catch (Exception e)
        {
            throw new InvalidDataException($"Unable to read {prefixed} property from DB config", e);
        }
    }
}
