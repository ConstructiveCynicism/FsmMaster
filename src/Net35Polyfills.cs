namespace System.Diagnostics.CodeAnalysis
{
    // net35's mscorlib predates C#'s nullable-reference-type analysis attributes entirely, so the
    // compiler has nothing to bind [NotNullWhen] to - this is a pure compile-time marker with no
    // runtime behavior, matching the real BCL type's shape exactly so usage elsewhere reads the same
    // as it would on a modern target.
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        public NotNullWhenAttribute(bool returnValue)
        {
            ReturnValue = returnValue;
        }

        public bool ReturnValue { get; }
    }
}

namespace FsmMaster
{
    // System.Array.Empty<T>() doesn't exist in net35's mscorlib (a .NET 4.6 addition) and
    // System.Array itself is sealed, so it can't be extended from here - callers use
    // ArrayPolyfill.Empty<T>() instead.
    internal static class ArrayPolyfill
    {
        public static T[] Empty<T>() => EmptyArray<T>.Value;

        private static class EmptyArray<T>
        {
            public static readonly T[] Value = new T[0];
        }
    }
}
