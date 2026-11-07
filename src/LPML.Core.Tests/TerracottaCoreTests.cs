using System.Collections.Generic;
using LPML.Core.Multiplayer;
using LPML.Core.Utils;
using Xunit;

namespace LPML.Core.Tests;

public sealed class TerracottaCoreTests
{
    [Fact]
    public void GenerateRoomCode_DefaultLength_IsEightAndUsesSafeAlphabet()
    {
        var code = TerracottaCore.GenerateRoomCode();
        Assert.Equal(8, code.Length);
        Assert.All(code, c => Assert.Contains(c, "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"));
    }

    [Fact]
    public void GenerateRoomCode_DistinctCodes_AreNotEqual_Statistically()
    {
        var set = new HashSet<string>();
        for (var i = 0; i < 200; i++) set.Add(TerracottaCore.GenerateRoomCode());
        Assert.True(set.Count > 190, "生成的房间号应当足够随机");
    }

    [Theory]
    [InlineData("tun0: inet 10.233.233.1  netmask 255.255.255.0", "10.233.233.1")]
    [InlineData("inet 192.168.5.20", "192.168.5.20")]
    [InlineData("VIRTUAL_IP=10.233.233.5", "10.233.233.5")]
    [InlineData("virtual ip: 172.20.1.9", "172.20.1.9")]
    [InlineData(" 169.254.10.20  link", "169.254.10.20")]
    [InlineData("127.0.0.1 localhost", "127.0.0.1")]
    public void TryParseVirtualIp_KnownFormats_ReturnsExpected(string line, string expected)
        => Assert.True(TerracottaCore.TryParseVirtualIp(line, out var ip) && ip == expected);

    [Theory]
    [InlineData("inet 8.8.8.8")]
    [InlineData("public address 1.1.1.1")]
    [InlineData("no ip here")]
    [InlineData("")]
    public void TryParseVirtualIp_NonPrivateOrGarbage_ReturnsFalse(string line)
        => Assert.False(TerracottaCore.TryParseVirtualIp(line, out _));

    public static IEnumerable<object[]> PeerLines =>
        new object[][] { new object[] { "2026-10-28 peer connected: 192.168.1.55 public-key A1B2C3D4E5F60011", "192.168.1.55", "A1B2C3D4E5F60011" } };

    [Theory]
    [MemberData(nameof(PeerLines))]
    public void TryParsePeer_PeerLineWithPrivateIp_Parsed(string line, string ip, string key)
    {
        var peer = TerracottaCore.TryParsePeer(line);
        Assert.NotNull(peer);
        Assert.Equal(ip, peer.Ip);
        Assert.Equal(key, peer.PublicKey);
    }

    [Fact]
    public void TryParsePeer_PublicIp_Ignored()
        => Assert.Null(TerracottaCore.TryParsePeer("peer 8.8.8.8 disconnected"));

    [Fact]
    public void TryParsePeer_NodeLineWithoutIp_ReturnsNull()
        => Assert.Null(TerracottaCore.TryParsePeer("node list is empty"));

    // HMCL 的 terracotta.json 用 ${version}，内置模板用 {version}；
    // 曾经只替换后者，结果拼出 v$0.4.2 导致四个源全部 404。
    [Theory]
    [InlineData("https://x/v${version}/terracotta-${version}-${classifier}-pkg.tar.gz",
                "https://x/v0.4.2/terracotta-0.4.2-windows-x86_64-pkg.tar.gz")]
    [InlineData("https://x/v{version}/terracotta-{version}-{classifier}-pkg.tar.gz",
                "https://x/v0.4.2/terracotta-0.4.2-windows-x86_64-pkg.tar.gz")]
    public void BuildUrl_HmclAndBuiltinTemplates_ExpandWithoutDollar(string template, string expected)
        => Assert.Equal(expected, TerracottaDefaults.BuildUrl(template, "0.4.2", "windows-x86_64"));

    [Theory]
    [InlineData("0.4.2", "0.4.2")]
    [InlineData("v0.4.2", "0.4.2")]
    [InlineData(" V1.2 ", "1.2")]
    public void NormalizeVersion_StripsLeadingV(string input, string expected)
        => Assert.Equal(expected, TerracottaDefaults.NormalizeVersion(input));

    [Fact]
    public void Start_NeedsInstalledBinary()
    {
        var paths = new AppPaths();
        paths.Apply(System.AppDomain.CurrentDomain.BaseDirectory, null, null);
        using var core = new TerracottaCore(paths, new HttpService(), new TerracottaSettings());
        Assert.False(core.IsInstalled);
        Assert.Throws<InvalidOperationException>(() => core.Start("ABCD1234"));
    }

    [Fact]
    public void Sha256String_MatchesWellKnownVector()
    {
        var actual = HashHelper.Sha256String("");
        Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", actual);
    }

    [Theory]
    [InlineData("U/YNZE-U61D-2206-HXRG", "U/YNZE-U61D-2206-HXRG")]
    [InlineData("YNZE-U61D-2206-HXRG", "U/YNZE-U61D-2206-HXRG")]
    [InlineData("u/ynze u61d 2206 hxrg", "U/YNZE-U61D-2206-HXRG")]
    [InlineData("U/YNIE-U61D-2206-HXRG", "U/YN1E-U61D-2206-HXRG")] // I -> 1
    [InlineData("U/YNOE-U61D-2206-HXRG", "U/YN0E-U61D-2206-HXRG")] // O -> 0
    public void NormalizeRoomCode_OfficialFormat_IsAccepted(string input, string expected)
    {
        Assert.Equal(expected, TerracottaCore.NormalizeRoomCode(input));
        Assert.True(TerracottaRoomCode.IsWellFormed(input));
    }

    [Theory]
    [InlineData("ABCD1234")]
    [InlineData("U/ABCD-1234")]
    [InlineData("")]
    [InlineData("U/AAAA-AAAA-AAAA-AAA!")]
    public void NormalizeRoomCode_LegacyOrMalformed_IsRejected(string input)
        => Assert.Equal("", TerracottaCore.NormalizeRoomCode(input));

    [Fact]
    public void GenerateOfficialRoomCode_IsWellFormedAndHasValidChecksum()
    {
        for (var i = 0; i < 64; i++)
        {
            var code = TerracottaCore.GenerateOfficialRoomCode();
            Assert.Equal(21, code.Length); // U/ + 16 位 + 3 个连字符
            Assert.True(TerracottaRoomCode.IsWellFormed(code));
            Assert.True(TerracottaRoomCode.HasValidChecksum(code));
        }
    }

    [Fact]
    public void StateParser_HostOk_ReadsRoomAndProfiles()
    {
        const string json = """
            {"state":"host-ok","index":4,"room":"U/YNZE-U61D-2206-HXRG",
             "profiles":[{"machine_id":"abc","name":"Player1","vendor":"LPML/1.0","kind":"HOST"}]}
            """;
        var state = TerracottaStateParser.Parse(json);
        Assert.Equal(TerracottaStateKind.HostOk, state.Kind);
        Assert.Equal(4L, state.Index);
        Assert.Equal("U/YNZE-U61D-2206-HXRG", state.RoomCode);
        Assert.Single(state.Profiles);
        Assert.Equal("LPML/1.0", state.Profiles[0].Vendor);
    }

    [Fact]
    public void StateParser_GuestOk_ReadsUrl()
    {
        const string json = """{"state":"guest-ok","index":7,"url":"127.0.0.1"}""";
        var state = TerracottaStateParser.Parse(json);
        Assert.Equal(TerracottaStateKind.GuestOk, state.Kind);
        Assert.Equal("127.0.0.1", state.Url);
    }
}
