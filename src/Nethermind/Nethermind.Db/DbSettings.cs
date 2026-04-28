// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Db
{
    public class DbSettings(string name, string path)
    {
        public string DbName { get; private set; } = name;
        public string DbPath { get; private set; } = path;

        public bool DeleteOnStart { get; set; }
        public bool CanDeleteFolder { get; set; } = true;

        public IMergeOperator? MergeOperator { get; set; }
        public Dictionary<string, IMergeOperator>? ColumnsMergeOperators { get; set; }

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
