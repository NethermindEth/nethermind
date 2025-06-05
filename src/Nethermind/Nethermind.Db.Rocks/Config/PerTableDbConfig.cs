// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;

namespace Nethermind.Db.Rocks.Config;

public class PerTableDbConfig
{
    private readonly string _tableName;
    private readonly string? _columnName;
    private readonly IDbConfig _dbConfig;
    private readonly DbSettings _settings;
    private readonly string[] _prefixes;
    private readonly string[] _reversedPrefixes;

    public PerTableDbConfig(IDbConfig dbConfig, DbSettings dbSettings, string? columnName = null)
    {
        _dbConfig = dbConfig;
        _settings = dbSettings;
        _tableName = _settings.DbName;
        _columnName = columnName;
        _prefixes = GetPrefixes();
        _reversedPrefixes = _prefixes.Reverse().ToArray();

#if DEBUG
        EnsureConfigIsAvailable(nameof(RocksDbOptions));
        EnsureConfigIsAvailable(nameof(AdditionalRocksDbOptions));
#endif
    }

    private void EnsureConfigIsAvailable(string propertyName)
    {
        Type type = typeof(IDbConfig);
        foreach (var prefix in _prefixes)
        {
            string prefixed = string.Concat(prefix, propertyName);
            if (type.GetProperty(prefixed, BindingFlags.Public | BindingFlags.Instance) is null)
            {
                throw new InvalidConfigurationException($"Configuration {propertyName} not available with prefix {prefix}. Add {prefix}{propertyName} to {nameof(IDbConfig)}.", -1);
            }
        }
    }

    public ulong? WriteBufferSize => ReadConfig<ulong?>(nameof(WriteBufferSize));
    public ulong? WriteBufferNumber => ReadConfig<ulong?>(nameof(WriteBufferNumber));

    public string RocksDbOptions => ReadRocksdbOptions(_dbConfig, nameof(RocksDbOptions), _prefixes);
    public string AdditionalRocksDbOptions => ReadRocksdbOptions(_dbConfig, nameof(AdditionalRocksDbOptions), _prefixes);

    public int? MaxOpenFiles => ReadConfig<int?>(nameof(MaxOpenFiles));
    public bool WriteAheadLogSync => ReadConfig<bool>(nameof(WriteAheadLogSync));
    public ulong? ReadAheadSize => ReadConfig<ulong?>(nameof(ReadAheadSize));
    public bool EnableDbStatistics => _dbConfig.EnableDbStatistics;
    public uint StatsDumpPeriodSec => _dbConfig.StatsDumpPeriodSec;
    public bool? VerifyChecksum => ReadConfig<bool?>(nameof(VerifyChecksum));
    public ulong? RowCacheSize => ReadConfig<ulong?>(nameof(RowCacheSize));
    public bool EnableFileWarmer => ReadConfig<bool>(nameof(EnableFileWarmer));
    public double CompressibilityHint => ReadConfig<double>(nameof(CompressibilityHint));
    public bool FlushOnExit => ReadConfig<bool?>(nameof(FlushOnExit)) ?? true;

    private T? ReadConfig<T>(string propertyName)
    {
        return ReadConfig<T>(_dbConfig, propertyName, _reversedPrefixes);
    }

    private string[] GetPrefixes()
    {
        if (_tableName.StartsWith("State"))
        {
            return ["StateDb"];
        }

        if (_columnName != null)
        {
            return [
                string.Concat(_tableName, "Db"),
                string.Concat(_tableName, _columnName, "Db"),
            ];
        }

        return [string.Concat(_tableName, "Db")];
    }

    private static string ReadRocksdbOptions(IDbConfig dbConfig, string propertyName, string[] prefixes)
    {
        Type type = dbConfig.GetType();
        PropertyInfo? propertyInfo;

        string val = (string)type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)!.GetValue(dbConfig)!;

        foreach (var prefix in prefixes)
        {
            string prefixed = string.Concat(prefix, propertyName);

            propertyInfo = type.GetProperty(prefixed, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (propertyInfo is not null)
            {
                string? valObj = (string?)propertyInfo.GetValue(dbConfig);
                if (!string.IsNullOrEmpty(valObj))
                {
                    if (!valObj.EndsWith(';')) throw new InvalidConfigurationException($"Rocksdb config must end with `;`. Invalid property is {propertyName} in {prefixed}.", -1);
                    val += valObj;
                }
            }
        }

        return val;
    }

    private static T? ReadConfig<T>(IDbConfig dbConfig, string propertyName, string[] prefixes)
    {
        try
        {
            Type type = dbConfig.GetType();
            PropertyInfo? propertyInfo;

            foreach (var prefix in prefixes)
            {
                string prefixed = string.Concat(prefix, propertyName);

                propertyInfo = type.GetProperty(prefixed, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (propertyInfo is not null)
                {
                    if (propertyInfo.PropertyType.CanBeAssignedNull())
                    {
                        // If its nullable check if its null first
                        object? valObj = propertyInfo.GetValue(dbConfig);
                        if (valObj is not null)
                        {
                            T? val = (T?)valObj;
                            if (val is not null)
                            {
                                return val;
                            }
                        }
                    }
                    else
                    {
                        // If not nullable just use it directly
                        return (T?)propertyInfo.GetValue(dbConfig);
                    }
                }
            }

            // Use generic one even if its available
            propertyInfo = type.GetProperty(propertyName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            return (T?)propertyInfo?.GetValue(dbConfig);
        }
        catch (Exception e)
        {
            throw new InvalidDataException($"Unable to read property from DB config. Prefixes: ${prefixes}", e);
        }
    }

}
