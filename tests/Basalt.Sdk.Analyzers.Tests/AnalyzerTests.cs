#pragma warning disable IL3000 // Assembly.Location returns empty string in single-file apps

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Basalt.Sdk.Analyzers;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Analyzers.Tests;

public class AnalyzerTests
{
    private static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync<TAnalyzer>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        // Gather all needed references from the runtime directory
        var runtimeDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
        };

        // Add essential runtime assemblies for successful compilation
        var additionalAssemblies = new[]
        {
            "System.Runtime.dll",
            "netstandard.dll",
            "System.Collections.dll",
            "System.Threading.dll",
            "System.Threading.Thread.dll",
            "System.Net.Http.dll",
            "System.IO.dll",
            "System.IO.FileSystem.dll",
            "System.Console.dll",
            "System.Net.Primitives.dll",
            "System.Net.Sockets.dll",
            "System.Threading.Tasks.dll",
        };

        foreach (var asm in additionalAssemblies)
        {
            var path = System.IO.Path.Combine(runtimeDir, asm);
            if (System.IO.File.Exists(path))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        var compilation = CSharpCompilation.Create("TestAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var analyzer = new TAnalyzer();
        var compilationWithAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));
        var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diagnostics;
    }

    // ========================================================================
    // BST001: NoReflectionAnalyzer
    // ========================================================================

    [Fact]
    public async Task BST001_UsingSystemReflection_WithContract_ReportsDiagnostic()
    {
        var source = @"
using System.Reflection;

[BasaltContract]
public class MyContract
{
    public void Foo() { }
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<NoReflectionAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST001");
    }

    [Fact]
    public async Task BST001_UsingSystemReflection_WithoutContract_NoDiagnostic()
    {
        var source = @"
using System.Reflection;

public class NotAContract
{
    public void Foo() { }
}
";
        var diags = await GetDiagnosticsAsync<NoReflectionAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    [Fact]
    public async Task BST001_GetMethodCall_InContract_ReportsDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public void Foo()
    {
        var t = typeof(string).GetMethod(""ToString"");
    }
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<NoReflectionAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST001");
    }

    [Fact]
    public async Task BST001_NoReflection_InContract_NoDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public int Add(int a, int b) => a + b;
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<NoReflectionAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    // ========================================================================
    // BST002: NoDynamicAnalyzer
    // ========================================================================

    [Fact]
    public async Task BST002_DynamicType_InContract_ReportsDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public void Foo()
    {
        dynamic x = 42;
    }
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<NoDynamicAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST002");
    }

    [Fact]
    public async Task BST002_NoDynamic_InContract_NoDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public int Add(int a, int b) => a + b;
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<NoDynamicAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    [Fact]
    public async Task BST002_DynamicType_OutsideContract_NoDiagnostic()
    {
        var source = @"
public class NotAContract
{
    public void Foo()
    {
        dynamic x = 42;
    }
}
";
        var diags = await GetDiagnosticsAsync<NoDynamicAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    // ========================================================================
    // BST003: DeterminismAnalyzer
    // The analyzer checks expressionText == "DateTime" (not "System.DateTime"),
    // so test source must use a 'using System;' and unqualified 'DateTime'.
    // ========================================================================

    [Fact]
    public async Task BST003_DateTimeNow_InContract_ReportsDiagnostic()
    {
        var source = @"
using System;
[BasaltContract]
public class MyContract
{
    public long GetTime() => DateTime.Now.Ticks;
}
public class BasaltContractAttribute : Attribute { }
";
        var diags = await GetDiagnosticsAsync<DeterminismAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST003");
    }

    [Fact]
    public async Task BST003_DateTimeUtcNow_InContract_ReportsDiagnostic()
    {
        var source = @"
using System;
[BasaltContract]
public class MyContract
{
    public long GetTime() => DateTime.UtcNow.Ticks;
}
public class BasaltContractAttribute : Attribute { }
";
        var diags = await GetDiagnosticsAsync<DeterminismAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST003");
    }

    [Fact]
    public async Task BST003_FloatType_InContract_ReportsDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public float Compute() => 3.14f;
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<DeterminismAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST003");
    }

    [Fact]
    public async Task BST003_DoubleType_InContract_ReportsDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public double Compute() => 3.14;
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<DeterminismAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST003");
    }

    [Fact]
    public async Task BST003_DecimalType_InContract_ReportsDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public decimal Compute() => 3.14m;
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<DeterminismAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST003");
    }

    [Fact]
    public async Task BST003_DeterministicCode_InContract_NoDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public int Add(int a, int b) => a + b;
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<DeterminismAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    [Fact]
    public async Task BST003_DateTimeNow_OutsideContract_NoDiagnostic()
    {
        var source = @"
using System;
public class NotAContract
{
    public long GetTime() => DateTime.Now.Ticks;
}
";
        var diags = await GetDiagnosticsAsync<DeterminismAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    [Fact]
    public async Task BST003_NewRandom_InContract_ReportsDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public int GetRandom()
    {
        var rng = new System.Random();
        return rng.Next();
    }
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<DeterminismAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST003");
    }

    [Fact]
    public async Task BST003_GuidNewGuid_InContract_ReportsDiagnostic()
    {
        var source = @"
using System;
[BasaltContract]
public class MyContract
{
    public string GetId() => Guid.NewGuid().ToString();
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<DeterminismAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST003");
    }

    [Fact]
    public async Task BST003_EnvironmentTickCount_InContract_ReportsDiagnostic()
    {
        var source = @"
using System;
[BasaltContract]
public class MyContract
{
    public int GetTicks() => Environment.TickCount;
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<DeterminismAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST003");
    }

    // ========================================================================
    // BST004: ReentrancyAnalyzer
    // ========================================================================

    [Fact]
    public async Task BST004_StorageWriteAfterExternalCall_ReportsDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    private StorageMap _balances = new StorageMap();

    public void Transfer()
    {
        Context.CallContract(""addr"", ""method"");
        _balances.Set(""key"", ""value"");
    }
}
public class BasaltContractAttribute : System.Attribute { }
public static class Context
{
    public static void CallContract(string addr, string method) { }
}
public class StorageMap
{
    public void Set(string key, string value) { }
}
";
        var diags = await GetDiagnosticsAsync<ReentrancyAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST004");
    }

    [Fact]
    public async Task BST004_StorageWriteBeforeExternalCall_NoDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    private StorageMap _balances = new StorageMap();

    public void Transfer()
    {
        _balances.Set(""key"", ""value"");
        Context.CallContract(""addr"", ""method"");
    }
}
public class BasaltContractAttribute : System.Attribute { }
public static class Context
{
    public static void CallContract(string addr, string method) { }
}
public class StorageMap
{
    public void Set(string key, string value) { }
}
";
        var diags = await GetDiagnosticsAsync<ReentrancyAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    [Fact]
    public async Task BST004_NoExternalCalls_NoDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public int Add(int a, int b) => a + b;
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<ReentrancyAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    // ========================================================================
    // BST005: OverflowAnalyzer
    // ========================================================================

    [Fact]
    public async Task BST005_UncheckedExpression_InContract_ReportsDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public int Overflow()
    {
        return unchecked(int.MaxValue + 1);
    }
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<OverflowAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST005");
    }

    [Fact]
    public async Task BST005_UncheckedStatement_InContract_ReportsDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public int Overflow()
    {
        unchecked { return int.MaxValue + 1; }
    }
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<OverflowAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST005");
    }

    [Fact]
    public async Task BST005_CheckedArithmetic_InContract_NoDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public int Add(int a, int b) => checked(a + b);
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<OverflowAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    [Fact]
    public async Task BST005_NoUnchecked_InContract_NoDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public int Add(int a, int b) => a + b;
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<OverflowAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    [Fact]
    public async Task BST005_UncheckedOutsideContract_NoDiagnostic()
    {
        var source = @"
public class NotAContract
{
    public int Overflow()
    {
        unchecked { return int.MaxValue + 1; }
    }
}
";
        var diags = await GetDiagnosticsAsync<OverflowAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    // ========================================================================
    // BST006: StorageAccessAnalyzer
    // ========================================================================

    [Fact]
    public async Task BST006_ContractStorageWrite_InContract_ReportsDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public void WriteData()
    {
        ContractStorage.Write(new byte[32], new byte[32]);
    }
}
public class BasaltContractAttribute : System.Attribute { }
public static class ContractStorage
{
    public static void Write(byte[] key, byte[] value) { }
    public static byte[] Read(byte[] key) => new byte[0];
}
";
        var diags = await GetDiagnosticsAsync<StorageAccessAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST006");
    }

    [Fact]
    public async Task BST006_ContractStorageRead_InContract_ReportsDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public byte[] ReadData()
    {
        return ContractStorage.Read(new byte[32]);
    }
}
public class BasaltContractAttribute : System.Attribute { }
public static class ContractStorage
{
    public static void Write(byte[] key, byte[] value) { }
    public static byte[] Read(byte[] key) => new byte[0];
}
";
        var diags = await GetDiagnosticsAsync<StorageAccessAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST006");
    }

    [Fact]
    public async Task BST006_NoStorageAccess_InContract_NoDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public int Add(int a, int b) => a + b;
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<StorageAccessAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    [Fact]
    public async Task BST006_ContractStorageAccess_OutsideContract_NoDiagnostic()
    {
        var source = @"
public class NotAContract
{
    public void WriteData()
    {
        ContractStorage.Write(new byte[32], new byte[32]);
    }
}
public static class ContractStorage
{
    public static void Write(byte[] key, byte[] value) { }
}
";
        var diags = await GetDiagnosticsAsync<StorageAccessAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    // ========================================================================
    // BST007: GasEstimationAnalyzer
    // ========================================================================

    [Fact]
    public async Task BST007_SimpleEntrypoint_ReportsBaseGas()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    [BasaltEntrypoint]
    public int Add(int a, int b) => a + b;
}
public class BasaltContractAttribute : System.Attribute { }
public class BasaltEntrypointAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<GasEstimationAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST007");
        // The base gas of a simple method should be 21000
        diags.Should().Contain(d => d.Id == "BST007" && d.GetMessage().Contains("21000"));
    }

    [Fact]
    public async Task BST007_MethodWithLoop_ReportsHigherGas()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    [BasaltEntrypoint]
    public int Sum(int n)
    {
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            total += i;
        }
        return total;
    }
}
public class BasaltContractAttribute : System.Attribute { }
public class BasaltEntrypointAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<GasEstimationAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST007");
        // 21000 base + 10000 for one loop = 31000
        diags.Should().Contain(d => d.Id == "BST007" && d.GetMessage().Contains("31000"));
    }

    [Fact]
    public async Task BST007_MethodWithoutEntrypointAttribute_NoDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public int Add(int a, int b) => a + b;
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<GasEstimationAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    [Fact]
    public async Task BST007_BasaltViewAttribute_ReportsGas()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    [BasaltView]
    public int GetValue() => 42;
}
public class BasaltContractAttribute : System.Attribute { }
public class BasaltViewAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<GasEstimationAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST007");
    }

    // ========================================================================
    // BST008: AotCompatibilityAnalyzer
    // ========================================================================

    [Fact]
    public async Task BST008_ThreadCreation_InContract_ReportsDiagnostic()
    {
        var source = @"
using System.Threading;
[BasaltContract]
public class MyContract
{
    public void StartThread()
    {
        var t = new Thread(() => { });
    }
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<AotCompatibilityAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST008");
    }

    [Fact]
    public async Task BST008_FileReadAllText_InContract_ReportsDiagnostic()
    {
        var source = @"
using System.IO;
[BasaltContract]
public class MyContract
{
    public string ReadConfig()
    {
        return File.ReadAllText(""config.json"");
    }
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<AotCompatibilityAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST008");
    }

    [Fact]
    public async Task BST008_ActivatorCreateInstance_InContract_ReportsDiagnostic()
    {
        var source = @"
using System;
[BasaltContract]
public class MyContract
{
    public object Create()
    {
        return Activator.CreateInstance(typeof(string));
    }
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<AotCompatibilityAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST008");
    }

    [Fact]
    public async Task BST008_TaskRun_InContract_ReportsDiagnostic()
    {
        var source = @"
using System.Threading.Tasks;
[BasaltContract]
public class MyContract
{
    public void DoWork()
    {
        Task.Run(() => { });
    }
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<AotCompatibilityAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST008");
    }

    [Fact]
    public async Task BST008_SafeCode_InContract_NoDiagnostic()
    {
        var source = @"
[BasaltContract]
public class MyContract
{
    public int Add(int a, int b) => a + b;
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<AotCompatibilityAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    [Fact]
    public async Task BST008_ThreadCreation_OutsideContract_NoDiagnostic()
    {
        var source = @"
using System.Threading;
public class NotAContract
{
    public void StartThread()
    {
        var t = new Thread(() => { });
    }
}
";
        var diags = await GetDiagnosticsAsync<AotCompatibilityAnalyzer>(source);
        diags.Should().BeEmpty();
    }

    [Fact]
    public async Task BST008_HttpClient_InContract_ReportsDiagnostic()
    {
        var source = @"
using System.Net.Http;
[BasaltContract]
public class MyContract
{
    public void MakeRequest()
    {
        var client = new HttpClient();
    }
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<AotCompatibilityAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST008");
    }

    [Fact]
    public async Task BST008_FileWriteAllText_InContract_ReportsDiagnostic()
    {
        var source = @"
using System.IO;
[BasaltContract]
public class MyContract
{
    public void WriteConfig()
    {
        File.WriteAllText(""config.json"", ""data"");
    }
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<AotCompatibilityAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST008");
    }

    [Fact]
    public async Task BST008_DirectoryCreateDirectory_InContract_ReportsDiagnostic()
    {
        var source = @"
using System.IO;
[BasaltContract]
public class MyContract
{
    public void CreateDir()
    {
        Directory.CreateDirectory(""mydir"");
    }
}
public class BasaltContractAttribute : System.Attribute { }
";
        var diags = await GetDiagnosticsAsync<AotCompatibilityAnalyzer>(source);
        diags.Should().Contain(d => d.Id == "BST008");
    }
}
