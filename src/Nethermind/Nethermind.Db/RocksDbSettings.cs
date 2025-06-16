// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db
{
    public class DbSettings
    {
        public DbSettings(string name, string path)
        {
            DbName = name;
            DbPath = path;
        }

        public string DbName { get; private set; }
        public string DbPath { get; private set; }

        public bool DeleteOnStart { get; set; }
        public bool CanDeleteFolder { get; set; } = true;
        public IMergeOperator? MergeOperator { get; set; }

        public DbSettings Clone(string name, string path)
        {
            DbSettings settings = (DbSettings)MemberwiseClone();
            settings.DbName = name;
            settings.DbPath = path;
            return settings;
        }

        public DbSettings Clone() => (DbSettings)MemberwiseClone();

        public override string ToString() => $"{DbName}:{DbPath}";
    }
}
