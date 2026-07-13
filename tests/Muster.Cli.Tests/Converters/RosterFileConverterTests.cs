using System.IO.Compression;
using System.Text;
using Muster.Cli.Converters;
using Xunit;

namespace Muster.Cli.Tests.Converters;

public class RosterFileConverterTests
{
    // Minimal valid BattleScribe roster XML. xmlns/battleScribeVersion match
    // Namespaces.RosterXmlns ("http://www.battlescribe.net/schema/rosterSchema")
    // and the wham sample asset lib/wham/src/Phalanx.SampleDataset/Test Roster 1.ros.
    private const string RosterXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <roster id="r-1" name="Test Roster" battleScribeVersion="2.03"
                gameSystemId="gs-1" gameSystemName="Test GS" gameSystemRevision="1"
                xmlns="http://www.battlescribe.net/schema/rosterSchema">
          <costs>
            <cost name="pts" typeId="pts-type" value="65.0"/>
          </costs>
          <forces>
            <force id="f-1" name="Patrol" entryId="fe-1" catalogueId="cat-1"
                   catalogueRevision="1" catalogueName="Test Cat">
              <selections>
                <selection id="s-1" name="Deathmarks" entryId="link-1::se-1"
                           number="1" type="unit">
                  <costs>
                    <cost name="pts" typeId="pts-type" value="65.0"/>
                  </costs>
                  <selections>
                    <selection id="s-2" name="Gauss blaster" entryId="se-2"
                               number="5" type="upgrade" customName="Fancy guns"/>
                  </selections>
                </selection>
              </selections>
            </force>
          </forces>
        </roster>
        """;

    [Fact]
    public void Converts_ros_xml_to_replay_roster()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(RosterXml));
        var roster = RosterFileConverter.Convert(stream, "test.ros");

        Assert.Equal("Test Roster", roster.Name);
        Assert.Equal("gs-1", roster.GameSystemId);
        var total = Assert.Single(roster.ObservedTotals);
        Assert.Equal(65.0m, total.Value);
        var force = Assert.Single(roster.Forces);
        Assert.Equal("fe-1", force.ForceEntryId);
        Assert.Equal("cat-1", force.CatalogueId);
        var unit = Assert.Single(force.Selections);
        Assert.Equal("link-1::se-1", unit.EntryId); // composite id preserved verbatim
        var unitCost = Assert.Single(unit.ObservedCosts);
        Assert.Equal(65.0m, unitCost.Value);
        var child = Assert.Single(unit.Children);
        Assert.Equal("se-2", child.EntryId);
        Assert.Equal(5, child.Count);
        Assert.Equal("Fancy guns", child.CustomName);
    }

    [Fact]
    public void Converts_rosz_zip()
    {
        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("test.ros").Open();
            entry.Write(Encoding.UTF8.GetBytes(RosterXml));
        }
        zipStream.Position = 0;

        var roster = RosterFileConverter.Convert(zipStream, "test.rosz");
        Assert.Equal("Test Roster", roster.Name);
    }

    [Fact]
    public void Garbage_input_throws_FormatException()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<not-a-roster/>"));
        Assert.Throws<FormatException>(() => RosterFileConverter.Convert(stream, "x.ros"));
    }

    // Second force ("Second Force") deliberately omits entryId.
    private const string RosterXmlWithOneBadForce = """
        <?xml version="1.0" encoding="UTF-8"?>
        <roster id="r-1" name="Test Roster" battleScribeVersion="2.03"
                gameSystemId="gs-1" gameSystemName="Test GS" gameSystemRevision="1"
                xmlns="http://www.battlescribe.net/schema/rosterSchema">
          <forces>
            <force id="f-1" name="Patrol" entryId="fe-1" catalogueId="cat-1"
                   catalogueRevision="1" catalogueName="Test Cat">
              <selections>
                <selection id="s-1" name="Deathmarks" entryId="link-1::se-1"
                           number="1" type="unit"/>
              </selections>
            </force>
            <force id="f-2" name="Second Force" catalogueId="cat-2"
                   catalogueRevision="1" catalogueName="Test Cat 2">
              <selections>
                <selection id="s-3" name="Warriors" entryId="link-2::se-3"
                           number="1" type="unit"/>
              </selections>
            </force>
          </forces>
        </roster>
        """;

    private const string RosterXmlWithOnlyBadForce = """
        <?xml version="1.0" encoding="UTF-8"?>
        <roster id="r-1" name="Test Roster" battleScribeVersion="2.03"
                gameSystemId="gs-1" gameSystemName="Test GS" gameSystemRevision="1"
                xmlns="http://www.battlescribe.net/schema/rosterSchema">
          <forces>
            <force id="f-1" name="Only Force" catalogueId="cat-1"
                   catalogueRevision="1" catalogueName="Test Cat">
              <selections>
                <selection id="s-1" name="Deathmarks" entryId="link-1::se-1"
                           number="1" type="unit"/>
              </selections>
            </force>
          </forces>
        </roster>
        """;

    [Fact]
    public void Force_with_no_entryId_is_skipped_with_Unmapped_note()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(RosterXmlWithOneBadForce));
        var roster = RosterFileConverter.Convert(stream, "test.ros");

        var force = Assert.Single(roster.Forces);
        Assert.Equal("fe-1", force.ForceEntryId);
        var note = Assert.Single(roster.Unmapped);
        Assert.Contains("Second Force", note);
    }

    [Fact]
    public void Roster_whose_only_force_has_no_entryId_throws_FormatException()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(RosterXmlWithOnlyBadForce));
        var ex = Assert.Throws<FormatException>(() => RosterFileConverter.Convert(stream, "test.ros"));
        Assert.Contains("no forces", ex.Message);
    }

    [Fact]
    public void Rosz_that_decompresses_over_50MB_throws_FormatException_fast()
    {
        // A large run of compressible whitespace crushes down to a tiny compressed
        // entry, but decompresses well past the 50 MB cap.
        var padding = new string(' ', 60 * 1024 * 1024);
        var xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><roster xmlns=\"http://www.battlescribe.net/schema/rosterSchema\">{padding}";

        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("test.ros", CompressionLevel.Optimal).Open();
            entry.Write(Encoding.UTF8.GetBytes(xml));
        }
        zipStream.Position = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<FormatException>(() => RosterFileConverter.Convert(zipStream, "test.rosz"));
        sw.Stop();

        Assert.Contains("too large", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }
}
