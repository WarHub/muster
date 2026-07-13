using System.Text.RegularExpressions;

namespace Muster.Cli.NewRecruit;

public static partial class NrShareLink
{
    [GeneratedRegex(@"^https://www\.newrecruit\.eu/app/list/([A-Za-z0-9]{1,32})/?$")]
    private static partial Regex Pattern();

    public static bool TryParse(string url, out string key)
    {
        key = "";
        if (string.IsNullOrWhiteSpace(url)) return false;
        var m = Pattern().Match(url.Trim());
        if (!m.Success) return false;
        key = m.Groups[1].Value;
        return true;
    }
}
