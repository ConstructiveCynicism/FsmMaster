#if NET35 || NET472
namespace System.Diagnostics.CodeAnalysis
{
    // Both net35's and net472's mscorlib predate C#'s nullable-reference-type analysis attributes
    // entirely (never backported to classic .NET Framework, any version), so the compiler has nothing
    // to bind [NotNullWhen] to - this is a pure compile-time marker with no runtime behavior, matching
    // the real BCL type's shape exactly so usage elsewhere reads the same as it would on a modern
    // target. Guarded off netstandard2.1 only, which already defines this attribute in its own
    // reference assemblies - an unconditional copy here would collide with it (CS0436).
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
#endif

#if NET35
namespace System.Runtime.CompilerServices
{
    // Emitted by the compiler on every member that returns or holds a *named*-element tuple (e.g.
    // `(Vector2 Position, Color Color)`) - purely a compile-time marker read by tooling, with no
    // runtime behavior of its own. Guarded to net35 only: net472 (unlike net35) already has
    // System.ValueTuple built into mscorlib since .NET Framework 4.7, tuple-attribute included, so an
    // unconditional copy on net472 would collide with it (CS0436) the same way it would on
    // netstandard2.1.
    [AttributeUsage(AttributeTargets.All)]
    public sealed class TupleElementNamesAttribute : Attribute
    {
        public TupleElementNamesAttribute(string[] transformNames)
        {
            TransformNames = transformNames;
        }

        public string[] TransformNames { get; }
    }
}

namespace System
{
    // net35 predates the ValueTuple family (a .NET 4.7/netstandard2.0 addition) that C# 7's tuple
    // literal syntax `(T1, T2)` lowers to - the compiler needs these exact type shapes (arities 1-8,
    // `Item1`..`ItemN` fields, `TRest` for the 8th slot) to compile any tuple literal on this TFM,
    // regardless of whether the tuple's elements end up named. Only what the compiler itself requires
    // is implemented - no IEquatable/IComparable/IStructuralEquatable, since nothing in this codebase
    // compares or boxes a tuple instance.
    public struct ValueTuple<T1>
    {
        public T1 Item1;
        public ValueTuple(T1 item1) => Item1 = item1;
    }

    public struct ValueTuple<T1, T2>
    {
        public T1 Item1;
        public T2 Item2;
        public ValueTuple(T1 item1, T2 item2) => (Item1, Item2) = (item1, item2);
    }

    public struct ValueTuple<T1, T2, T3>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public ValueTuple(T1 item1, T2 item2, T3 item3) => (Item1, Item2, Item3) = (item1, item2, item3);
    }

    public struct ValueTuple<T1, T2, T3, T4>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4) =>
            (Item1, Item2, Item3, Item4) = (item1, item2, item3, item4);
    }

    public struct ValueTuple<T1, T2, T3, T4, T5>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;
        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5) =>
            (Item1, Item2, Item3, Item4, Item5) = (item1, item2, item3, item4, item5);
    }

    public struct ValueTuple<T1, T2, T3, T4, T5, T6>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;
        public T6 Item6;
        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6) =>
            (Item1, Item2, Item3, Item4, Item5, Item6) = (item1, item2, item3, item4, item5, item6);
    }

    public struct ValueTuple<T1, T2, T3, T4, T5, T6, T7>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;
        public T6 Item6;
        public T7 Item7;
        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7) =>
            (Item1, Item2, Item3, Item4, Item5, Item6, Item7) = (item1, item2, item3, item4, item5, item6, item7);
    }

    public struct ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;
        public T6 Item6;
        public T7 Item7;
        public TRest Rest;
        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7, TRest rest) =>
            (Item1, Item2, Item3, Item4, Item5, Item6, Item7, Rest) = (item1, item2, item3, item4, item5, item6, item7, rest);
    }
}
#endif

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
