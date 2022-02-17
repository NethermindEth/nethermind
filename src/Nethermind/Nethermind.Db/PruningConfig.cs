//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

namespace Nethermind.Db
{
    public class PruningConfig : IPruningConfig
    {
        public bool Enabled
        {
            get => Mode.IsMemory();
            set
            {
                if (value)
                {
                    Mode |= PruningMode.Memory;
                }
                else
                {
                    Mode &= ~PruningMode.Memory;
                }
            }
        }

        public PruningMode Mode { get; set; } = PruningMode.None;
        public long CacheMb { get; set; } = 512;
        public long PersistenceInterval { get; set; } = 8192;
        public long FullPruningThresholdMb { get; set; } = 256000;
        public FullPruningTrigger FullPruningTrigger { get; set; } = FullPruningTrigger.Manual;
        public int FullPruningMaxDegreeOfParallelism { get; set; } = 0;
        public int FullPruningMinimumDelayHours { get; set; } = 240;
    }
}
