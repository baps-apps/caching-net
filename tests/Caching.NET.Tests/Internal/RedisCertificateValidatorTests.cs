using System.Net.Security;
using Caching.NET.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caching.NET.Tests.Internal;

public class RedisCertificateValidatorTests
{
    [Fact]
    public void Validate_with_no_errors_returns_true_and_records_ok()
    {
        var validator = new RedisCertificateValidator(NullLogger<RedisCertificateValidator>.Instance, strict: true);
        Assert.True(validator.Validate(this, certificate: null, chain: null, SslPolicyErrors.None));
    }

    [Fact]
    public void Validate_with_name_mismatch_strict_returns_false()
    {
        var validator = new RedisCertificateValidator(NullLogger<RedisCertificateValidator>.Instance, strict: true);
        Assert.False(validator.Validate(this, null, null, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void Validate_with_name_mismatch_non_strict_returns_true()
    {
        var validator = new RedisCertificateValidator(NullLogger<RedisCertificateValidator>.Instance, strict: false);
        Assert.True(validator.Validate(this, null, null, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void Validate_with_chain_error_returns_false_regardless_of_strict()
    {
        var validator = new RedisCertificateValidator(NullLogger<RedisCertificateValidator>.Instance, strict: false);
        Assert.False(validator.Validate(this, null, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
