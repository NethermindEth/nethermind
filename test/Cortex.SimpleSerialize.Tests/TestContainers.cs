using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize.Tests.Containers;

namespace Cortex.SimpleSerialize.Tests
{
    namespace Containers
    {
        // Define containers as plain classes

        internal class FixedTestContainer
        {
            public byte A { get; set; }
            public ulong B { get; set; }
            public uint C { get; set; }
        }

        internal class SingleFieldTestContainer
        {
            public byte A { get; set; }
        }

        internal class SmallTestContainer
        {
            public ushort A { get; set; }
            public ushort B { get; set; }
        }

        internal class VarTestContainer
        {
            public ushort A { get; set; }
            public IList<ushort> B { get; set; } // max = 1024
            public byte C { get; set; }
        }

        //class ComplexTestContainer
        //{
        //    public ushort A { get; set; }
        //    public IList<ushort> B { get; set; } // max = 128
        //    public byte C { get; set; }
        //    public byte[] D { get; set; } // length = 256
        //    public VarTestContainer E { get; set; }
        //    public FixedTestContainer[] F { get; set; }// length = 4
        //    public VarTestContainer[] G { get; set; } // length = 2
        //}
    }

    namespace Ssz
    {
        // Define builder extensions that construct SSZ elements from containers

        internal static class FixedTestContainerExtensions
        {
            public static SszContainer ToSszContainer(this FixedTestContainer item)
            {
                return new SszContainer(GetValues(item));
            }

            private static IEnumerable<SszElement> GetValues(FixedTestContainer item)
            {
                yield return new SszBasicElement(item.A);
                yield return new SszBasicElement(item.B);
                yield return new SszBasicElement(item.C);
            }
        }

        internal static class SingleFieldTestContainerExtensions
        {
            public static SszContainer ToSszContainer(this SingleFieldTestContainer item)
            {
                return new SszContainer(GetChildren(item));
            }

            public static SszElement ToSszList(this IEnumerable<SingleFieldTestContainer> list, ulong limit)
            {
                return new SszList(list.Select(x => x.ToSszContainer()), limit);
            }

            private static IEnumerable<SszElement> GetChildren(SingleFieldTestContainer item)
            {
                yield return new SszBasicElement(item.A);
            }
        }

        internal static class SmallTestContainerExtensions
        {
            public static SszContainer ToSszContainer(this SmallTestContainer item)
            {
                return new SszContainer(GetChildren(item));
            }

            private static IEnumerable<SszElement> GetChildren(SmallTestContainer item)
            {
                yield return new SszBasicElement(item.A);
                yield return new SszBasicElement(item.B);
            }
        }

        internal static class VarTestContainerExtensions
        {
            public static SszContainer ToSszContainer(this VarTestContainer item)
            {
                return new SszContainer(GetChildren(item));
            }

            private static IEnumerable<SszElement> GetChildren(VarTestContainer item)
            {
                yield return new SszBasicElement(item.A);
                yield return new SszBasicList(item.B.ToArray(), limit: 1024);
                yield return new SszBasicElement(item.C);
            }
        }
    }
}
