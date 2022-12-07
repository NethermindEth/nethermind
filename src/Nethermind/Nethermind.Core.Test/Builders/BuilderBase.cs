// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;

namespace Nethermind.Core.Test.Builders
{
    /// <summary>
    /// This class is here just to hint the API for implementations
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public abstract class BuilderBase<T>
    {
        protected internal T TestObjectInternal { get; set; } = default!;

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
