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

using System.Linq;

namespace Nethermind.Core.Test.Builders
{
    /// <summary>
    /// This class is here just to hint the API for implementations
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public abstract class BuilderBase<T>
    {
        protected internal T TestObjectInternal { get; set; }

        public T TestObject
        {
            get
            {
                BeforeReturn();
                return TestObjectInternal;
            }

            protected set => TestObjectInternal = value;
        }

        public T[] TestObjectNTimes(int n)
        {
            T testObject = TestObject;
            return Enumerable.Repeat(testObject, n).ToArray();
        }

        protected virtual void BeforeReturn()
        {
        }
    }
}
