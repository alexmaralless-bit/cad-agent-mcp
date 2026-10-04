// Polyfill for C# 9+ `init`-only setters on .NET Framework 4.8.
// The compiler requires this type to be present in any referenced assembly
// when emitting `init` accessors. Modern .NET (5+) ships it in mscorlib;
// .NET Framework does not. Defining it ourselves with `internal` visibility
// satisfies the compiler without leaking into the public API surface.
//
// Standard pattern, see:
// https://github.com/dotnet/roslyn/issues/45510#issuecomment-694977239

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
