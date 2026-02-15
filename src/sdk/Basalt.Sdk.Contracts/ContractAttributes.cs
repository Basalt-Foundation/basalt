namespace Basalt.Sdk.Contracts;

/// <summary>
/// Marks a class as a Basalt smart contract.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class BasaltContractAttribute : Attribute;

/// <summary>
/// Marks a method as a contract entrypoint (state-mutating).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BasaltEntrypointAttribute : Attribute;

/// <summary>
/// Marks a method as a view function (read-only, no gas).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BasaltViewAttribute : Attribute;

/// <summary>
/// Marks a method as a contract constructor (called once on deploy).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BasaltConstructorAttribute : Attribute;

/// <summary>
/// Marks a class as a contract event.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class BasaltEventAttribute : Attribute;

/// <summary>
/// Marks a property as an indexed event parameter for efficient querying.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IndexedAttribute : Attribute;

/// <summary>
/// Marks a type for automatic binary codec generation (WriteTo/ReadFrom/GetSerializedSize).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class BasaltSerializableAttribute : Attribute;

/// <summary>
/// Marks a type for automatic JSON serialization support with Basalt primitive converters.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class BasaltJsonSerializableAttribute : Attribute;
