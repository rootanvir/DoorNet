using DoorNet.Core.Threats;

namespace DoorNet.Core.Tests;

public sealed class UrlAnalyzerTests
{
    [Theory]
    [InlineData("https://www.google.com/search?q=door")]
    [InlineData("https://github.com/dotnet/runtime/issues/1")]
    [InlineData("https://login.microsoftonline.com/common/oauth2")]
    [InlineData("https://example.com/docs/getting-started")]
    public void OrdinarySitesScoreZero(string url)
    {
        var result = UrlAnalyzer.Assess(url);

        Assert.False(result.IsSuspicious);
        Assert.True(result.Score < UrlAnalyzer.MediumScore, $"{url} scored {result.Score}");
    }

    [Theory]
    [InlineData("chrome://settings")]
    [InlineData("about:blank")]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("http://localhost:5080/health")]
    [InlineData("http://192.168.0.1/admin")]
    [InlineData("http://10.1.2.3:8080/")]
    [InlineData("http://nas.lan/files")]
    [InlineData("http://printer.local/status")]
    public void LocalAndBrowserAddressesAreExempt(string url)
    {
        var result = UrlAnalyzer.Assess(url);

        Assert.True(result.IsExempt);
        Assert.False(result.IsSuspicious);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void RawAddressHostIsFlagged()
    {
        var result = UrlAnalyzer.Assess("http://185.220.101.5/login");

        Assert.Contains(result.Signals, signal => signal.Id == "ip-host");
        Assert.True(result.IsSuspicious);
    }

    [Fact]
    public void CredentialsTrickIsFlagged()
    {
        var result = UrlAnalyzer.Assess("https://microsoft.com@evil-host.tld/account");

        Assert.Contains(result.Signals, signal => signal.Id == "userinfo");
        Assert.True(result.IsSuspicious);
    }

    [Fact]
    public void LookalikeBrandIsFlagged()
    {
        var result = UrlAnalyzer.Assess("https://paypa1.com/signin");

        Assert.Contains(result.Signals, signal => signal.Id == "brand-lookalike");
        Assert.True(result.IsSuspicious);
    }

    [Fact]
    public void BrandInSubdomainIsFlagged()
    {
        var result = UrlAnalyzer.Assess("https://microsoft.com.security-check.today/verify");

        Assert.Contains(result.Signals, signal => signal.Id == "brand-subdomain");
        Assert.True(result.IsSuspicious);
    }

    [Fact]
    public void BrandOwnDomainIsNotFlagged()
    {
        var result = UrlAnalyzer.Assess("https://paypal.com/signin");

        Assert.DoesNotContain(result.Signals, signal => signal.Id is "brand-lookalike" or "brand-subdomain");
        Assert.False(result.IsSuspicious);
    }

    [Theory]
    [InlineData("http://secure-account.xyz/login")]
    [InlineData("http://update-check.top/verify")]
    public void AbusedTopLevelDomainAddsWeight(string url)
    {
        var result = UrlAnalyzer.Assess(url);

        Assert.Contains(result.Signals, signal => signal.Id == "bad-tld");
        Assert.True(result.Score >= UrlAnalyzer.MediumScore);
    }

    [Fact]
    public void DynamicDnsHostIsFlagged()
    {
        var result = UrlAnalyzer.Assess("http://door-net.duckdns.org:9000/panel");

        Assert.Contains(result.Signals, signal => signal.Id == "ddns");
        Assert.Contains(result.Signals, signal => signal.Id == "odd-port");
        Assert.True(result.IsSuspicious);
    }

    [Fact]
    public void PlainHttpLoginPageAddsWeight()
    {
        var result = UrlAnalyzer.Assess("http://example.com/secure/billing/login");

        Assert.Contains(result.Signals, signal => signal.Id == "http-login");
        Assert.True(result.Score >= 20);
        Assert.False(result.IsSuspicious);
    }

    [Fact]
    public void DisguisedExecutableIsFlagged()
    {
        var result = UrlAnalyzer.Assess("https://files.example.com/invoice.pdf.exe");

        Assert.Contains(result.Signals, signal => signal.Id == "double-extension");
        Assert.True(result.IsSuspicious);
    }

    [Fact]
    public void OrdinaryDocumentDownloadIsNotFlagged()
    {
        var result = UrlAnalyzer.Assess("https://files.example.com/report.pdf");

        Assert.DoesNotContain(result.Signals, signal => signal.Id == "double-extension");
    }

    [Fact]
    public void RandomLookingHostIsFlagged()
    {
        var result = UrlAnalyzer.Assess("http://x7k2m9q4v1z8p3w6.example.com/");

        Assert.Contains(result.Signals, signal => signal.Id == "random-label");
    }

    [Fact]
    public void DeepSubdomainAddsWeight()
    {
        var result = UrlAnalyzer.Assess("https://a.b.c.d.example.com/");

        Assert.Contains(result.Signals, signal => signal.Id == "deep-subdomain");
    }

    [Fact]
    public void UncommonPortAddsWeight()
    {
        var result = UrlAnalyzer.Assess("https://example.com:8443/admin");

        Assert.Contains(result.Signals, signal => signal.Id == "odd-port");
    }

    [Fact]
    public void PunycodeHostIsFlagged()
    {
        var result = UrlAnalyzer.Assess("https://xn--pypal-4ve.com/login");

        Assert.Contains(result.Signals, signal => signal.Id == "idn");
        Assert.True(result.IsSuspicious);
    }

    [Fact]
    public void ProcessRunningFromTempAddsWeight()
    {
        var result = UrlAnalyzer.Assess(
            "http://example.com/payload",
            @"C:\Users\me\AppData\Local\Temp\svc\update.exe");

        Assert.Contains(result.Signals, signal => signal.Id == "temp-process");
    }

    [Fact]
    public void ScoreIsCappedAndSeverityMatches()
    {
        var result = UrlAnalyzer.Assess("http://user@paypa1.xyz:8080/secure/login/invoice.pdf.exe");

        Assert.True(result.Score <= 100);
        Assert.Equal(ThreatSeverity.Critical, result.Severity);
        Assert.True(result.Signals.Count >= 4);
    }

    [Fact]
    public void GarbageInputIsExemptRatherThanThrowing()
    {
        Assert.True(UrlAnalyzer.Assess(null).IsExempt);
        Assert.True(UrlAnalyzer.Assess("   ").IsExempt);
        Assert.True(UrlAnalyzer.Assess("not a url").IsExempt);
        Assert.True(UrlAnalyzer.Assess("javascript:alert(1)").IsExempt);
    }

    [Fact]
    public void ReasonsAreHumanReadableAndMatchSignals()
    {
        var result = UrlAnalyzer.Assess("http://185.220.101.5/login");

        Assert.Equal(result.Signals.Count, result.Reasons.Split(" · ").Length);
        Assert.Contains("Direct IP address", result.Reasons);
    }

    [Fact]
    public void CatalogIsConsistent()
    {
        Assert.NotEmpty(UrlAnalyzer.Rules);
        Assert.All(UrlAnalyzer.Rules, rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Title));
            Assert.InRange(rule.Weight, 1, 100);
        });
    }
}
