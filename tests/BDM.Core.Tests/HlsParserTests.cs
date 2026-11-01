using BDM.Core.Streams;

namespace BDM.Core.Tests;

public class HlsParserTests
{
    static readonly Uri Base = new("https://cdn.example/a/b/index.m3u8?token=1");

    const string Master = """
        #EXTM3U
        #EXT-X-VERSION:6
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="English",LANGUAGE="en",URI="audio/en.m3u8"
        #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="English",LANGUAGE="en",URI="subs/en.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2",AUDIO="aud",SUBTITLES="subs"
        low/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=2400000,RESOLUTION=1280x720,CODECS="avc1.4d401f,mp4a.40.2",AUDIO="aud"
        high/index.m3u8?q=1
        #EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=100000,URI="iframe.m3u8"
        """;

    [Fact]
    public void Master_ParsesVariantsAndAudioGroups()
    {
        var p = HlsParser.Parse(Master, Base);
        Assert.True(p.IsMaster);
        Assert.Equal(2, p.Variants.Count);
        Assert.Equal(new Uri("https://cdn.example/a/b/low/index.m3u8"), p.Variants[0].Url);
        Assert.Equal(800000, p.Variants[0].Bandwidth);
        Assert.Equal("640x360", p.Variants[0].Resolution);
        Assert.Equal("avc1.4d401e,mp4a.40.2", p.Variants[0].Codecs); // comma inside quotes survives
        Assert.Equal("aud", p.Variants[0].AudioGroup);
        Assert.Equal("subs", p.Variants[0].SubtitleGroup);
        Assert.Equal(new Uri("https://cdn.example/a/b/high/index.m3u8?q=1"), p.Variants[1].Url);
        var audio = p.Media.Single(m => m.Type == "AUDIO");
        Assert.Equal("aud", audio.GroupId);
        Assert.Equal("en", audio.Language);
        Assert.Equal(new Uri("https://cdn.example/a/b/audio/en.m3u8"), audio.Url);
        Assert.Equal(2, p.Media.Count);
    }

    [Fact]
    public void Media_RelativeAndQueryUrlsResolveAgainstBase()
    {
        var p = HlsParser.Parse("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.006,Title here\nseg1.ts?x=2\n#EXTINF:5,\n/abs/seg2.ts\n#EXTINF:5,\n../up/seg3.ts\n#EXTINF:5,\nhttps://other.example/seg4.ts\n#EXT-X-ENDLIST\n", Base);
        Assert.False(p.IsMaster);
        Assert.Equal(new[]
        {
            "https://cdn.example/a/b/seg1.ts?x=2", "https://cdn.example/abs/seg2.ts", "https://cdn.example/a/up/seg3.ts", "https://other.example/seg4.ts",
        }, p.Segments.Select(s => s.Url.ToString()));
        Assert.Equal(6.006, p.Segments[0].Duration, 3);
        Assert.Equal(6, p.TargetDuration);
        Assert.Equal(new long[] { 0, 1, 2, 3 }, p.Segments.Select(s => s.Sequence));
        Assert.True(p.Ended);
    }

    [Fact]
    public void Segments_ByteRangeWithoutOffsetContinuesPreviousRange()
    {
        var p = HlsParser.Parse("#EXTM3U\n#EXTINF:4,\n#EXT-X-BYTERANGE:1000@0\nbig.ts\n#EXTINF:4,\n#EXT-X-BYTERANGE:500\nbig.ts\n#EXTINF:4,\n#EXT-X-BYTERANGE:200@5000\nbig.ts\n#EXT-X-ENDLIST\n", Base);
        Assert.Equal((0L, 1000L), (p.Segments[0].ByteRangeOffset!.Value, p.Segments[0].ByteRangeLength!.Value));
        Assert.Equal((1000L, 500L), (p.Segments[1].ByteRangeOffset!.Value, p.Segments[1].ByteRangeLength!.Value));
        Assert.Equal((5000L, 200L), (p.Segments[2].ByteRangeOffset!.Value, p.Segments[2].ByteRangeLength!.Value));
    }

    [Fact]
    public void Key_Aes128_ExplicitIv_And_ImplicitIvFromSequence()
    {
        const string text = """
            #EXTM3U
            #EXT-X-MEDIA-SEQUENCE:7
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x000102030405060708090A0B0C0D0E0F
            #EXTINF:4,
            a.ts
            #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example/k2"
            #EXTINF:4,
            b.ts
            #EXT-X-KEY:METHOD=NONE
            #EXTINF:4,
            c.ts
            #EXT-X-ENDLIST
            """;
        var p = HlsParser.Parse(text, Base);
        var a = p.Segments[0].Key!;
        Assert.Equal("AES-128", a.Method);
        Assert.Equal(new Uri("https://cdn.example/a/b/key.bin"), a.Uri);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i), a.Iv!);
        Assert.Equal(a.Iv, a.IvFor(7));
        var b = p.Segments[1].Key!;
        Assert.Null(b.Iv);
        var iv = b.IvFor(8); // implicit IV = sequence number as 128-bit big-endian
        Assert.Equal(16, iv.Length);
        Assert.Equal(8, iv[15]);
        Assert.All(iv.Take(15), x => Assert.Equal(0, x));
        Assert.Null(p.Segments[2].Key); // METHOD=NONE clears the key
    }

    [Fact]
    public void Key_MethodSampleAes_FlaggedUnsupported()
    {
        var p = HlsParser.Parse("#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://x\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n", Base);
        var k = p.Segments[0].Key!;
        Assert.Equal("SAMPLE-AES", k.Method);
        Assert.False(k.IsSupported);
        Assert.True(new HlsKey("AES-128", null, null).IsSupported);
    }

    [Fact]
    public void Fmp4_ExtXMap_Parsed()
    {
        var p = HlsParser.Parse("#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\",BYTERANGE=\"720@0\"\n#EXTINF:4,\ns1.m4s\n#EXT-X-ENDLIST\n", Base);
        Assert.Equal(new Uri("https://cdn.example/a/b/init.mp4"), p.Map!.Url);
        Assert.Equal(0, p.Map.ByteRangeOffset);
        Assert.Equal(720, p.Map.ByteRangeLength);
    }

    [Fact]
    public void Live_NoEndlist_EndedFalse()
    {
        var p = HlsParser.Parse("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:100\n#EXTINF:4,\ns100.ts\n#EXTINF:4,\ns101.ts\n", Base);
        Assert.False(p.Ended);
        Assert.Equal(100, p.Segments[0].Sequence);
    }

    [Fact]
    public void Tolerates_BomAndCrlfAndBlankLines()
    {
        var p = HlsParser.Parse("﻿#EXTM3U\r\n\r\n#EXTINF:4,\r\na.ts\r\n#EXT-X-ENDLIST\r\n", Base);
        Assert.Single(p.Segments);
        Assert.True(p.Ended);
    }

    [Fact]
    public void Garbage_NotM3u8_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => HlsParser.Parse("<html><body>Not found</body></html>", Base));
        Assert.Throws<FormatException>(() => HlsParser.Parse("", Base));
    }
}
