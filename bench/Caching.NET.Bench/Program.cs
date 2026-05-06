using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

return BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args).ToArray().ToExitCode();

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
