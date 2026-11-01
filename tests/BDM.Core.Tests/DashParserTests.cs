using BDM.Core.Streams;

namespace BDM.Core.Tests;

public class DashParserTests
{
    static readonly Uri Base = new("https://cdn.example/vod/stream/manifest.mpd?sig=abc");
    const string Ns = "urn:mpeg:dash:schema:mpd:2011";

    [Fact]
    public void Template_Number_ExpandsAllSegments()
    {
        var xml = $"""
            <MPD xmlns="{Ns}" type="static" mediaPresentationDuration="PT20S">
              <Period>
                <AdaptationSet mimeType="video/mp4" contentType="video">
                  <SegmentTemplate timescale="1000" duration="4000" startNumber="1" media="v_$RepresentationID$_$Number%03d$.m4s" initialization="init_$RepresentationID$.mp4"/>
                  <Representation id="v1" bandwidth="1000000" width="1280" height="720" codecs="avc1.64001f"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        var m = DashParser.Parse(xml, Base);
        var r = Assert.Single(Assert.Single(m.Periods).Representations);
        Assert.False(m.IsDynamic);
        Assert.Equal("v1", r.Id);
        Assert.Equal("video/mp4", r.MimeType);
        Assert.Equal(1_000_000, r.Bandwidth);
        Assert.Equal((1280, 720), (r.Width!.Value, r.Height!.Value));
        Assert.Equal("avc1.64001f", r.Codecs);
        Assert.Equal(5, r.SegmentUrls.Count);
        Assert.Equal(new Uri("https://cdn.example/vod/stream/v_v1_001.m4s"), r.SegmentUrls[0]);
        Assert.Equal(new Uri("https://cdn.example/vod/stream/v_v1_005.m4s"), r.SegmentUrls[4]);
        Assert.Equal(new Uri("https://cdn.example/vod/stream/init_v1.mp4"), r.InitUrl);
        Assert.Equal(20, r.DurationSeconds, 3);
        Assert.False(r.Protected);
    }

    [Fact]
    public void Timeline_RepeatCountExpands()
    {
        var xml = $"""
            <MPD xmlns="{Ns}" type="static">
              <Period>
                <AdaptationSet mimeType="video/mp4">
                  <Representation id="v" bandwidth="500000">
                    <SegmentTemplate timescale="1000" media="t_$Time$.m4s" initialization="i.mp4">
                      <SegmentTimeline><S t="0" d="2000" r="2"/><S d="1000"/></SegmentTimeline>
                    </SegmentTemplate>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        var r = DashParser.Parse(xml, Base).Periods[0].Representations[0];
        Assert.Equal(new[] { "t_0.m4s", "t_2000.m4s", "t_4000.m4s", "t_6000.m4s" }, r.SegmentUrls.Select(u => u.Segments.Last()));
        Assert.Equal(7, r.DurationSeconds, 3);
    }

    [Fact]
    public void Timeline_NegativeRepeat_RunsToEndOfPeriod()
    {
        var xml = $"""
            <MPD xmlns="{Ns}" type="static" mediaPresentationDuration="PT10S">
              <Period><AdaptationSet mimeType="video/mp4">
                <Representation id="v" bandwidth="1"><SegmentTemplate timescale="1" media="s$Number$.m4s" startNumber="0">
                  <SegmentTimeline><S t="0" d="2" r="-1"/></SegmentTimeline></SegmentTemplate></Representation>
              </AdaptationSet></Period>
            </MPD>
            """;
        Assert.Equal(5, DashParser.Parse(xml, Base).Periods[0].Representations[0].SegmentUrls.Count);
    }

    [Fact]
    public void SegmentList_And_SingleBaseUrl_Mp4()
    {
        var xml = $"""
            <MPD xmlns="{Ns}" type="static" mediaPresentationDuration="PT8S">
              <Period>
                <AdaptationSet mimeType="video/mp4">
                  <Representation id="list" bandwidth="1">
                    <SegmentList duration="4">
                      <Initialization sourceURL="init.mp4"/><SegmentURL media="a1.m4s"/><SegmentURL media="a2.m4s"/>
                    </SegmentList>
                  </Representation>
                </AdaptationSet>
                <AdaptationSet mimeType="audio/mp4" lang="en">
                  <Representation id="aud" bandwidth="128000"><BaseURL>audio.mp4</BaseURL><SegmentBase indexRange="0-800"/></Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        var reps = DashParser.Parse(xml, Base).Periods[0].Representations;
        Assert.Equal(new Uri("https://cdn.example/vod/stream/init.mp4"), reps[0].InitUrl);
        Assert.Equal(2, reps[0].SegmentUrls.Count);
        Assert.Equal(new Uri("https://cdn.example/vod/stream/a2.m4s"), reps[0].SegmentUrls[1]);
        Assert.Equal(new[] { new Uri("https://cdn.example/vod/stream/audio.mp4") }, reps[1].SegmentUrls);
        Assert.Null(reps[1].InitUrl);
        Assert.Equal("en", reps[1].Language);
        Assert.Equal("audio/mp4", reps[1].MimeType);
    }

    [Fact]
    public void MultiPeriod_ConcatenatesInOrder()
    {
        var xml = $"""
            <MPD xmlns="{Ns}" type="static">
              <Period id="p1" duration="PT8S"><AdaptationSet mimeType="video/mp4"><SegmentTemplate timescale="1" duration="4" media="p1_$Number$.m4s"/><Representation id="v" bandwidth="1"/></AdaptationSet></Period>
              <Period id="p2" duration="PT4S"><AdaptationSet mimeType="video/mp4"><SegmentTemplate timescale="1" duration="4" media="p2_$Number$.m4s"/><Representation id="v" bandwidth="1"/></AdaptationSet></Period>
            </MPD>
            """;
        var m = DashParser.Parse(xml, Base);
        Assert.Equal(2, m.Periods.Count);
        Assert.Equal(new[] { "p1_1.m4s", "p1_2.m4s" }, m.Periods[0].Representations[0].SegmentUrls.Select(u => u.Segments.Last()));
        Assert.Equal(new[] { "p2_1.m4s" }, m.Periods[1].Representations[0].SegmentUrls.Select(u => u.Segments.Last()));
    }

    [Fact]
    public void BaseUrl_NestedResolution()
    {
        var xml = $"""
            <MPD xmlns="{Ns}" type="static" mediaPresentationDuration="PT4S">
              <BaseURL>https://media.example/root/</BaseURL>
              <Period><BaseURL>p1/</BaseURL>
                <AdaptationSet mimeType="video/mp4"><BaseURL>v/</BaseURL>
                  <SegmentTemplate timescale="1" duration="4" media="seg$Number$.m4s"/>
                  <Representation id="a" bandwidth="1"/>
                  <Representation id="b" bandwidth="2"><BaseURL>https://other.example/abs/</BaseURL></Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        var reps = DashParser.Parse(xml, Base).Periods[0].Representations;
        Assert.Equal(new Uri("https://media.example/root/p1/v/seg1.m4s"), reps[0].SegmentUrls[0]);
        Assert.Equal(new Uri("https://other.example/abs/seg1.m4s"), reps[1].SegmentUrls[0]);
    }

    [Fact]
    public void ContentProtection_Widevine_MarksProtected()
    {
        var xml = $"""
            <MPD xmlns="{Ns}" type="static" mediaPresentationDuration="PT4S">
              <Period>
                <AdaptationSet mimeType="video/mp4">
                  <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"/>
                  <SegmentTemplate timescale="1" duration="4" media="e$Number$.m4s"/><Representation id="enc" bandwidth="1"/>
                </AdaptationSet>
                <AdaptationSet mimeType="audio/mp4">
                  <SegmentTemplate timescale="1" duration="4" media="c$Number$.m4s"/><Representation id="clear" bandwidth="1"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        var reps = DashParser.Parse(xml, Base).Periods[0].Representations;
        Assert.True(reps.Single(r => r.Id == "enc").Protected);
        Assert.False(reps.Single(r => r.Id == "clear").Protected);
    }

    [Fact]
    public void Dynamic_Mpd_IsDynamicTrue() =>
        Assert.True(DashParser.Parse($"<MPD xmlns=\"{Ns}\" type=\"dynamic\"><Period/></MPD>", Base).IsDynamic);

    [Fact]
    public void Instagram_style_separate_video_and_audio_adaptation_sets_listed_separately()
    {
        var xml = $"""
            <MPD xmlns="{Ns}" type="static" mediaPresentationDuration="PT15.5S">
              <Period>
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <Representation id="v720" bandwidth="1800000" width="720" height="1280" codecs="avc1.4d401f">
                    <BaseURL>https://scontent.cdninstagram.com/v/t50/video.mp4?efg=eyJ2&amp;oh=00_a&amp;oe=6543&amp;bytestart=0</BaseURL>
                    <SegmentBase indexRange="0-900"/>
                  </Representation>
                </AdaptationSet>
                <AdaptationSet contentType="audio" mimeType="audio/mp4">
                  <Representation id="a1" bandwidth="64000" codecs="mp4a.40.2">
                    <BaseURL>https://scontent.cdninstagram.com/v/t50/audio.mp4?efg=eyJh&amp;oh=00_b&amp;oe=6543</BaseURL>
                    <SegmentBase indexRange="0-500"/>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        var reps = DashParser.Parse(xml, Base).Periods[0].Representations;
        Assert.Equal(new[] { "video/mp4", "audio/mp4" }, reps.Select(r => r.MimeType));
        Assert.Equal("https://scontent.cdninstagram.com/v/t50/video.mp4?efg=eyJ2&oh=00_a&oe=6543&bytestart=0", reps[0].SegmentUrls[0].ToString());
        Assert.Equal("https://scontent.cdninstagram.com/v/t50/audio.mp4?efg=eyJh&oh=00_b&oe=6543", reps[1].SegmentUrls[0].ToString());
        Assert.Equal(15.5, reps[0].DurationSeconds, 3);
    }

    [Fact]
    public void Garbage_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => DashParser.Parse("#EXTM3U\n", Base));
        Assert.Throws<FormatException>(() => DashParser.Parse("<html/>", Base));
        Assert.Throws<FormatException>(() => DashParser.Parse("", Base));
    }
}
