using System.Diagnostics;

namespace Basalt.Cli;

/// <summary>
/// Manages the lifecycle of a Basalt.Node child process for DevNet mode.
/// Finds the node binary, starts it with dev-mode environment variables,
/// streams its output, and handles graceful shutdown on Ctrl+C.
/// </summary>
internal sealed class DevNodeLauncher : IDisposable
{
    private Process? _process;

    /// <summary>
    /// Start the Basalt.Node in DevNet mode.
    /// </summary>
    /// <param name="port">HTTP port for the REST API.</param>
    /// <param name="autoMine">If true, blocks are produced when transactions arrive.</param>
    /// <param name="blockTimeMs">Block interval in ms (ignored if autoMine is true).</param>
    /// <param name="accounts">Number of dev accounts to create.</param>
    /// <returns>The running process, or null if the node could not be started.</returns>
    public async Task<int> StartAsync(int port, bool autoMine, int blockTimeMs, int accounts)
    {
        var nodeProject = FindNodeProject();
        if (nodeProject == null)
        {
            Console.Error.WriteLine("Could not find Basalt.Node project. Set BASALT_NODE_PATH or run from the repo root.");
            return 1;
        }

        var env = new Dictionary<string, string>
        {
            ["BASALT_MODE"] = "dev",
            ["BASALT_AUTOMINE"] = autoMine ? "true" : "false",
            ["HTTP_PORT"] = port.ToString(),
            ["ASPNETCORE_URLS"] = $"http://localhost:{port}",
            ["BASALT_DEBUG"] = "1",
            ["BASALT_DEV_ACCOUNTS"] = accounts.ToString(),
            ["BASALT_CHAIN_ID"] = "31337",
            ["BASALT_LOG_LEVEL"] = "Warning", // Suppress info logs, banner is printed by DevNet mode
        };

        if (!autoMine && blockTimeMs > 0)
            env["BASALT_BLOCK_TIME"] = blockTimeMs.ToString();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{nodeProject}\" --no-launch-profile",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var (key, value) in env)
            psi.Environment[key] = value;

        _process = Process.Start(psi);
        if (_process == null)
        {
            Console.Error.WriteLine("Failed to start Basalt.Node process.");
            return 1;
        }

        // Forward Ctrl+C to the child process for graceful shutdown
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { _process?.Kill(entireProcessTree: true); } catch { /* already exited */ }
        };

        // Stream stdout/stderr to console
        var stdoutTask = Task.Run(async () =>
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
                Console.WriteLine(line);
        });

        var stderrTask = Task.Run(async () =>
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
                Console.Error.WriteLine(line);
        });

        await _process.WaitForExitAsync();
        await Task.WhenAll(stdoutTask, stderrTask);

        return _process.ExitCode;
    }

    /// <summary>
    /// Find the Basalt.Node project directory.
    /// Search order: BASALT_NODE_PATH env var, then walk up from CWD looking for the repo structure.
    /// </summary>
    private static string? FindNodeProject()
    {
        // Check env var first
        var envPath = Environment.GetEnvironmentVariable("BASALT_NODE_PATH");
        if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath))
            return envPath;

        // Walk up from the CLI's assembly directory looking for the solution structure
        var current = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(current, "src", "node", "Basalt.Node");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Basalt.Node.csproj")))
                return candidate;

            var parent = Directory.GetParent(current)?.FullName;
            if (parent == null || parent == current) break;
            current = parent;
        }

        // Try relative from CWD (developer running from repo root)
        var cwd = Directory.GetCurrentDirectory();
        var fromCwd = Path.Combine(cwd, "src", "node", "Basalt.Node");
        if (Directory.Exists(fromCwd) && File.Exists(Path.Combine(fromCwd, "Basalt.Node.csproj")))
            return fromCwd;

        return null;
    }

    public void Dispose()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }
        _process?.Dispose();
    }
}
