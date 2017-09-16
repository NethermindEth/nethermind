namespace Nevermind.Core
{
    public static class DecimalExtensions
    {
        public static decimal Ether(this decimal @this)
        {
            return @this * Unit.Ether;
        }
    }

    public static class IntExtensions
    {
        public static decimal Ether(this int @this)
        {
            return @this * Unit.Ether;
        }
    }
}