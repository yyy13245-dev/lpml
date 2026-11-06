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
}
