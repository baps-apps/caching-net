using Microsoft.CodeAnalysis;
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
        verifier.TestState.AdditionalReferences.Add(
            MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Logging.ILogger).Assembly.Location));
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
    public async Task KeyValuePair_Create_forbidden_key_reports_CN0001()
    {
        var src = Preamble + @"
class Caller
{
    static void X()
    {
        Test.C.Add(1, [|KeyValuePair.Create(""tenant"", (object?)""acme"")|]);
    }
}";
        await CreateVerifier(src).RunAsync();
    }

    [Fact]
    public async Task Const_folded_key_reports_CN0001()
    {
        var src = Preamble + @"
class Caller
{
    const string K = ""user_id"";
    static void X()
    {
        Test.C.Add(1, [|new KeyValuePair<string, object?>(K, 1)|]);
    }
}";
        await CreateVerifier(src).RunAsync();
    }

    [Fact]
    public async Task Logger_message_template_with_forbidden_placeholder_reports_CN0001()
    {
        var src = @"
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

class Test
{
    static readonly Meter M = new(""x"");
    public static readonly Counter<long> C = M.CreateCounter<long>(""x"");
}

class Caller
{
    static void X(ILogger log)
    {
        log.LogInformation([|""Hello {tenant} here""|]);
    }
}";
        await CreateVerifier(src).RunAsync();
    }

    [Fact]
    public async Task Logger_message_template_const_string_with_forbidden_placeholder_reports_CN0001()
    {
        var src = @"
using Microsoft.Extensions.Logging;

class Caller
{
    const string T = ""Hello {cache.key}"";
    static void X(ILogger log)
    {
        log.LogInformation([|T|]);
    }
}";
        await CreateVerifier(src).RunAsync();
    }

    [Fact]
    public async Task BeginScope_const_format_with_forbidden_placeholder_reports_CN0001()
    {
        var src = @"
using Microsoft.Extensions.Logging;

class Caller
{
    const string Scope = ""scope {user_id}"";
    static void X(ILogger log)
    {
        using var _ = log.BeginScope([|Scope|]);
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
