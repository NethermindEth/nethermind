namespace Nevermind.Core
{
    public static class IntExtensions
    {
        public static decimal Ether(this int @this)
        {
            return @this * Unit.Ether;
        }
    }
}