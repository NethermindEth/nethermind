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

using System.Threading.Tasks;

namespace Nethermind.Overseer.Test.Framework.Steps
{
    public abstract class TestStepBase
    {
        public string Name { get; }

        private static int _order = 1;

        protected TestStepBase(string name)
        {
            Name = name;
        }

        public abstract Task<TestResult> ExecuteAsync();

        protected TestResult GetResult(bool passed) => new TestResult(_order++, Name, passed);
    }
}
