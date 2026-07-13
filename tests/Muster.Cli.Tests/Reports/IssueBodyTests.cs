using Muster.Cli.Reports;
using Xunit;

namespace Muster.Cli.Tests.Reports;

public class IssueBodyTests
{
    [Fact]
    public void Parses_nr_auto_report()
    {
        // Verbatim shape of New Recruit auto-filed issues (BSData/wh40k-11e#234)
        var body = """
            **Problem:**
            Outriders Squad 6 members - 145 points

            **Expected:**
            Outriders Squad 6 members - 140 points

            **List:** https://www.newrecruit.eu/app/list/tr5BL
            **NewRecruit Version:** 34.99
            """;
        var parsed = IssueBody.Parse(body);

        Assert.Equal(RosterSourceKind.NrLink, parsed.Roster!.Kind);
        Assert.Equal("https://www.newrecruit.eu/app/list/tr5BL", parsed.Roster.Value);
        Assert.Contains("145 points", parsed.Problem, StringComparison.Ordinal);
        Assert.Contains("140 points", parsed.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_attachment_url()
    {
        var body = "My roster: https://github.com/user-attachments/files/12345/my-list.rosz broken!";
        var parsed = IssueBody.Parse(body);
        Assert.Equal(RosterSourceKind.Attachment, parsed.Roster!.Kind);
        Assert.EndsWith("my-list.rosz", parsed.Roster.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_inline_yaml_block()
    {
        var body = """
            Points look wrong:

            ```yaml
            steps:
              - action: addForce
                forceEntryId: "fe-1"
            ```
            """;
        var parsed = IssueBody.Parse(body);
        Assert.Equal(RosterSourceKind.InlineYaml, parsed.Roster!.Kind);
        Assert.Contains("addForce", parsed.Roster.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Nr_link_wins_over_attachment_and_yaml()
    {
        var body = """
            https://github.com/user-attachments/files/1/x.rosz
            **List:** https://www.newrecruit.eu/app/list/abc12
            ```yaml
            steps: []
            ```
            """;
        Assert.Equal(RosterSourceKind.NrLink, IssueBody.Parse(body).Roster!.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("just words, no roster anywhere")]
    [InlineData("https://evil.example/app/list/abc12")]
    [InlineData("```yaml\nname: no steps here\n```")]
    public void No_roster_source_yields_null(string body) =>
        Assert.Null(IssueBody.Parse(body).Roster);

    [Fact]
    public void Huge_body_is_truncated_not_hung()
    {
        var body = new string('a', 10_000_000) + " **List:** https://www.newrecruit.eu/app/list/abc12";
        var parsed = IssueBody.Parse(body); // must return fast
        Assert.Null(parsed.Roster); // link is beyond the 64 KiB parse window
    }

    [Fact]
    public void Parses_issue_form_style_headings()
    {
        // GitHub renders issue-form fields as `### Label` headings rather than
        // NR auto-report's `**Label:**` markers.
        var body = """
            ### Roster

            https://www.newrecruit.eu/app/list/abc12

            ### Problem

            Unit costs 65

            ### Expected

            Unit costs 60
            """;
        var parsed = IssueBody.Parse(body);

        Assert.Equal(RosterSourceKind.NrLink, parsed.Roster!.Kind);
        Assert.Equal("https://www.newrecruit.eu/app/list/abc12", parsed.Roster.Value);
        Assert.Contains("Unit costs 65", parsed.Problem, StringComparison.Ordinal);
        Assert.Contains("Unit costs 60", parsed.Expected, StringComparison.Ordinal);
    }
}
