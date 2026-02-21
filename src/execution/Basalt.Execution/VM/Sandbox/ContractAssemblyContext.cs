using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;

namespace Basalt.Execution.VM.Sandbox;

/// <summary>
/// A collectible <see cref="AssemblyLoadContext"/> used to isolate contract assemblies.
/// Each contract invocation gets its own context so that the loaded code can be fully
/// unloaded when execution completes, preventing memory leaks across calls.
///
/// The context is created with <c>isCollectible: true</c> to enable unloading.
/// Assembly names are validated against an allow-list before loading.
/// </summary>
public sealed class ContractAssemblyContext : AssemblyLoadContext
{
    /// <summary>
    /// Set of assembly simple names that contracts are permitted to reference.
    /// Any attempt to resolve an assembly outside this set will be rejected.
    /// </summary>
    public static readonly HashSet<string> AllowedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Basalt.Core",
        "Basalt.Sdk.Contracts",
        "Basalt.Codec",
        "System.Runtime",
        "System.Private.CoreLib",
        "netstandard",
    };

    private int _loadedAssemblyCount;

    /// <summary>
    /// Number of assemblies loaded into this context.
    /// </summary>
    public int LoadedAssemblyCount => _loadedAssemblyCount;

    /// <summary>
    /// Create a new contract assembly context.
    /// </summary>
    /// <param name="name">
    /// A human-readable name for debugging (typically the contract code hash).
    /// </param>
    public ContractAssemblyContext(string name)
        : base(name, isCollectible: true)
    {
    }

    /// <summary>
    /// Override the default assembly resolution.
    /// H-13: Actively rejects non-allowed assemblies instead of falling through to the
    /// default context, preventing transitive resolution of disallowed dependencies
    /// (e.g., System.IO.FileSystem, System.Net.Http, System.Diagnostics.Process).
    /// </summary>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // H-13: Block transitive resolution of non-allowed assemblies
        if (!IsAssemblyAllowed(assemblyName))
        {
            throw new SandboxIsolationException(
                $"Transitive resolution of assembly '{assemblyName.Name}' is blocked. " +
                $"Only the following assemblies are permitted: {string.Join(", ", AllowedAssemblyNames)}.");
        }

        // For allowed assemblies, fall through to the default context
        return null;
    }

    /// <summary>
    /// Check whether the given assembly name is on the allow-list.
    /// </summary>
    /// <param name="name">The assembly name to validate.</param>
    /// <returns><c>true</c> if the assembly is permitted; <c>false</c> otherwise.</returns>
    public static bool IsAssemblyAllowed(AssemblyName name)
    {
        var simpleName = name.Name;
        if (string.IsNullOrEmpty(simpleName))
            return false;

        return AllowedAssemblyNames.Contains(simpleName);
    }

    /// <summary>
    /// Load an assembly from raw bytes after validating that it is permitted.
    /// Increments the loaded assembly counter.
    /// </summary>
    /// <param name="assemblyBytes">The IL bytes of the assembly to load.</param>
    /// <returns>The loaded <see cref="Assembly"/>.</returns>
    /// <exception cref="SandboxIsolationException">
    /// Thrown if the assembly references disallowed dependencies.
    /// </exception>
    [RequiresUnreferencedCode("Contract assembly loading is inherently dynamic. Referenced types may be trimmed.")]
    public Assembly LoadAndValidate(byte[] assemblyBytes)
    {
        using var stream = new MemoryStream(assemblyBytes);
        var assembly = LoadFromStream(stream);

        // Validate that all referenced assemblies are on the allow-list
        foreach (var referencedAssembly in assembly.GetReferencedAssemblies())
        {
            if (!IsAssemblyAllowed(referencedAssembly))
            {
                throw new SandboxIsolationException(
                    $"Contract references disallowed assembly '{referencedAssembly.Name}'. " +
                    $"Only the following assemblies are permitted: {string.Join(", ", AllowedAssemblyNames)}.");
            }
        }

        Interlocked.Increment(ref _loadedAssemblyCount);
        return assembly;
    }
}
