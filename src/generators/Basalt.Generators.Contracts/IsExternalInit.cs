namespace System.Runtime.CompilerServices;

/// <summary>
/// Present in .NET 5 and later, absent from netstandard2.0, which is the target a Roslyn generator has
/// to build for. Declaring it here is what lets this project use records, and their value equality is
/// what makes the incremental generator cache work: without it every keystroke regenerates every event.
/// </summary>
internal static class IsExternalInit;
