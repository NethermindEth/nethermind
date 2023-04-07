// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Nethermind.Core.Extensions;

namespace Nethermind.Db.Rocks.Config;

public class PerTableDbConfig
{
    private readonly string _tableName;
    private readonly IDbConfig _dbConfig;
    private readonly RocksDbSettings _settings;

    public PerTableDbConfig(IDbConfig dbConfig, RocksDbSettings rocksDbSettings, string? columnName = null)
    {
        _dbConfig = dbConfig;
        _settings = rocksDbSettings;
        _tableName = _settings.DbName;
        if (columnName != null)
        {
            _tableName += columnName;
        }
    }

    public bool CacheIndexAndFilterBlocks => _settings.CacheIndexAndFilterBlocks ?? ReadConfig<bool>(nameof(CacheIndexAndFilterBlocks));

    public ulong BlockCacheSize => _settings.BlockCacheSize ?? ReadConfig<ulong>(nameof(BlockCacheSize));

    public ulong WriteBufferSize => _settings.WriteBufferSize ?? ReadConfig<ulong>(nameof(WriteBufferSize));

    public ulong WriteBufferNumber => _settings.WriteBufferNumber ?? ReadConfig<uint>(nameof(WriteBufferNumber));

    public IDictionary<string, string>? AdditionalRocksDbOptions => ReadConfig<IDictionary<string, string>?>(nameof(AdditionalRocksDbOptions));

    public int? MaxOpenFiles => ReadConfig<int?>(nameof(MaxOpenFiles));
    public long? MaxWriteBytesPerSec => ReadConfig<long?>(nameof(MaxWriteBytesPerSec));
    public uint RecycleLogFileNum => ReadConfig<uint>(nameof(RecycleLogFileNum));
    public bool WriteAheadLogSync => ReadConfig<bool>(nameof(WriteAheadLogSync));
    public bool EnableDbStatistics => _dbConfig.EnableDbStatistics;
    public uint StatsDumpPeriodSec => _dbConfig.StatsDumpPeriodSec;

    private T? ReadConfig<T>(string propertyName)
    {
        return ReadConfig<T>(_dbConfig, propertyName, GetPrefix());
    }

    private string GetPrefix()
    {
        return _tableName.StartsWith("State") ? "State" : string.Concat(_tableName, "Db");
    }

    private static T? ReadConfig<T>(IDbConfig dbConfig, string propertyName, string prefix)
    {
        string prefixed = string.Concat(prefix, propertyName);

        try
        {
            Type type = dbConfig.GetType();
            PropertyInfo? propertyInfo = type.GetProperty(prefixed, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

            if (propertyInfo != null && propertyInfo.PropertyType.CanBeAssignedNull())
            {
                // If its nullable check if its null first
                T? val = (T?)propertyInfo?.GetValue(dbConfig);
                if (val != null)
                {
                    return val;
                }

                // Use generic one even if its available
                propertyInfo = type.GetProperty(propertyName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            }

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
