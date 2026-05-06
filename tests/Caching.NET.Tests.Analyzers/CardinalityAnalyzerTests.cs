using Microsoft.CodeAnalysis.Testing;
using Xunit;

using Verifier = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerTest<
    Caching.NET.Analyzers.CardinalityAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Caching.NET.Tests.Analyzers;

public class CardinalityAnalyzerTests
{
    private const string Preamble = @"
using System.Collections.Generic;
using System.Diagnostics.Metrics;

class Test
{
    static readonly Meter M = new(""x"");
    public static readonly Counter<long> C = M.CreateCounter<long>(""x"");
}
";

    private static Verifier CreateVerifier(string testCode)
    {
        var verifier = new Verifier { TestCode = testCode };
        verifier.ReferenceAssemblies = ReferenceAssemblies.Net.Net80;
        return verifier;
    }

    [Fact]
    public async Task Forbidden_key_tag_reports_CN0001()
    {
        var src = Preamble + @"
class Caller
{
    static void X()
    {
        Test.C.Add(1, [|new KeyValuePair<string, object?>(""key"", ""abc"")|]);
    }
}";
        await CreateVerifier(src).RunAsync();
    }

    [Fact]
    public async Task Forbidden_tenant_tag_reports_CN0001()
    {
        var src = Preamble + @"
class Caller
{
    static void X()
    {
        Test.C.Add(1, [|new KeyValuePair<string, object?>(""tenant"", ""acme"")|]);
    }
}";
        await CreateVerifier(src).RunAsync();
    }

    [Fact]
    public async Task Allowed_mode_tag_does_not_report()
    {
        var src = Preamble + @"
class Caller
{
    static void X()
    {
        Test.C.Add(1, new KeyValuePair<string, object?>(""cache.mode"", ""Redis""));
    }
}";
        await CreateVerifier(src).RunAsync();
    }

    [Fact]
    public async Task Non_instrument_method_does_not_report()
    {
        var src = @"
using System.Collections.Generic;
class Other
{
    public void Add(int x, KeyValuePair<string, object?> p) { }
}
class Caller
{
    static void X()
    {
        new Other().Add(1, new KeyValuePair<string, object?>(""key"", ""abc""));
    }
}";
        await CreateVerifier(src).RunAsync();
    }
}
