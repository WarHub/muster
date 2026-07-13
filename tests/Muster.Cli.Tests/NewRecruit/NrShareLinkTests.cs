using Muster.Cli.NewRecruit;
using Xunit;

namespace Muster.Cli.Tests.NewRecruit;

public class NrShareLinkTests
{
    [Theory]
    [InlineData("https://www.newrecruit.eu/app/list/3Pbpd", true, "3Pbpd")]
    [InlineData("https://www.newrecruit.eu/app/list/3Pbpd/", true, "3Pbpd")]
    [InlineData("https://www.newrecruit.eu/app/list/tr5BL", true, "tr5BL")]
    [InlineData("http://www.newrecruit.eu/app/list/3Pbpd", false, "")]
    [InlineData("https://newrecruit.eu/app/list/3Pbpd", false, "")]
    [InlineData("https://evil.example/app/list/3Pbpd", false, "")]
    [InlineData("https://www.newrecruit.eu/app/list/../secret", false, "")]
    [InlineData("https://www.newrecruit.eu/app/list/3Pbpd?x=1", false, "")]
    [InlineData("https://www.newrecruit.eu/api/rpc", false, "")]
    [InlineData("not a url", false, "")]
    public void TryParse_allowlist(string url, bool ok, string key)
    {
        Assert.Equal(ok, NrShareLink.TryParse(url, out var k));
        if (ok) Assert.Equal(key, k);
    }
}
