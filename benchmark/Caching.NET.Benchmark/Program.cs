using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using System.IO;

var artifactsPath = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory,
    "..",
    "..",
    "..",
    "..",
    "BenchmarkDotNet.Artifacts"));
var config = DefaultConfig.Instance.WithArtifactsPath(artifactsPath);

return BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config).ToArray().ToExitCode();

internal static class Ext
{
    public static int ToExitCode(this Summary[] summaries)
    {
        foreach (var s in summaries)
            if (s.HasCriticalValidationErrors || s.HasAnyErrors()) return 1;
        return 0;
    }

    public static bool HasAnyErrors(this Summary s)
        => s.Reports.Any(r => !r.Success);
}
