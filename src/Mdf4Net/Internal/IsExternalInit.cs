#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices;

// Lets the netstandard2.0 build use `init` accessors. The compiler only needs the type to exist.
internal static class IsExternalInit
{
}
#endif
