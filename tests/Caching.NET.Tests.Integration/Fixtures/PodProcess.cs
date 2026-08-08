using System.Diagnostics;
using System.Reflection;

namespace Caching.NET.Tests.Integration.Fixtures;

/// <summary>
/// Drives a <c>Caching.NET.Tests.Pod</c> child process: one cache instance in its own OS process,
/// commanded over stdin and answering on stdout.
/// </summary>
internal sealed class PodProcess : IAsyncDisposable
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(60);

    private readonly Process _process;

    private PodProcess(Process process)
    {
        _process = process;
    }

    public static async Task<PodProcess> StartAsync(string mode, string applicationPrefix, string connectionString)
    {
        var podAssembly = ResolvePodAssembly();

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add(podAssembly);
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(applicationPrefix);
        startInfo.ArgumentList.Add(connectionString);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start the pod process for '{podAssembly}'.");

        var pod = new PodProcess(process);
        var ready = await pod.ReadAsync();
        if (ready != "ready")
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Pod did not start. First line: '{ready}'. Stderr: {error}");
        }

        return pod;
    }

    public async Task<string> SendAsync(string command)
    {
        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.FlushAsync();
        return await ReadAsync();
    }

    private async Task<string> ReadAsync()
    {
        using var timeout = new CancellationTokenSource(ResponseTimeout);
        var line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
        return line ?? throw new InvalidOperationException("The pod process closed its output stream.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteLineAsync("exit");
                await _process.StandardInput.FlushAsync();
                _process.StandardInput.Close();

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
        {
            // The pod is a test fixture: a stuck or already-dead child must never fail the run on
            // the way out, so fall through to the kill below.
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();
    }

    // The pod path is stamped into the test assembly by MSBuild, so it survives the test runner's
    // working directory and the per-TFM output layout.
    private static string ResolvePodAssembly()
    {
        var path = typeof(PodProcess).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "PodAssemblyPath")?.Value;

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                "PodAssemblyPath assembly metadata is missing. The integration test project must stamp it in.");
        }

        var normalized = Path.GetFullPath(path);
        if (!File.Exists(normalized))
        {
            throw new InvalidOperationException(
                $"The pod assembly was not built at '{normalized}'. Build the solution before running the multi-pod tests.");
        }

        return normalized;
    }
}
