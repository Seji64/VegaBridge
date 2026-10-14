using System.Text;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Models.Navigation;
using VegaBridgeApp.Services.BLE;
using VegaBridgeApp.Services.BLE.Plugins;
using Xunit;

namespace VegaBridgeApp.Tests;

/// <summary>How the MV Agusta plugin uses the BLE session (HAL) and which icon keys the bike gets.</summary>
public class MvAgustaBlePluginTests
{
    private static readonly Guid Service = Guid.Parse("00003719-0000-1000-8000-00805f9b34fb");
    private const string ReadCharacteristic = "00001234-0000-1000-8000-00805f9b34fb";
    private const string WriteCharacteristic = "00002345-0000-1000-8000-00805f9b34fb";

    [Fact]
    public async Task InitializeSession_SubscribesReadCharacteristic_AndIsReadyRightAway()
    {
        FakeSession session = new();

        bool ready = await new MvAgustaBlePlugin().InitializeSessionAsync(session, CancellationToken.None);

        Assert.True(ready);
        (Guid service, string characteristic) = Assert.Single(session.Subscriptions.Keys);
        Assert.Equal(Service, service);
        Assert.Equal(ReadCharacteristic, characteristic);
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task Frames_AreWriteCommandsOnTheWriteCharacteristic()
    {
        FakeSession session = new();

        await new MvAgustaBlePlugin().SendNavigationFinishAsync(session);

        FakeSession.Write write = Assert.Single(session.Writes);
        Assert.Equal(Service, write.Service);
        Assert.Equal(WriteCharacteristic, write.Characteristic);
        Assert.False(write.WithResponse);
        Assert.Equal("\rFINISH\u001E\u001E\u001E\r", Encoding.UTF8.GetString(write.Data));
    }

    [Fact]
    public async Task IncomingFrames_AreOnlyLogged()
    {
        FakeSession session = new();
        await new MvAgustaBlePlugin().InitializeSessionAsync(session, CancellationToken.None);
        Action<byte[]> onData = session.Subscriptions[(Service, ReadCharacteristic)];

        onData(Encoding.UTF8.GetBytes("\rGUI1\u001E2500 00BA040000 00\u001E\u001E\r"));
        onData([0x01, 0x02]); // not a frame

        // The phone never answers the bike's stream during navigation.
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(NavigationIconMapper.IconKeepLeft, "turn-slight-left")]
    [InlineData(NavigationIconMapper.IconRampRight, "turn-slight-right")]
    [InlineData(NavigationIconMapper.IconMergeLeft, "turn-slight-left")]
    [InlineData(NavigationIconMapper.IconExitRight, "turn-slight-right")]
    [InlineData(NavigationIconMapper.IconSharpLeft, "uturn-left")]
    [InlineData(NavigationIconMapper.IconWaypoint, "straight")]
    [InlineData(NavigationIconMapper.IconFerry, "straight")]
    [InlineData(NavigationIconMapper.IconFinish, "Finish")]
    public async Task Navi_FoldsSemanticIconsIntoTheBikeKeys(string semanticIcon, string bikeKey)
    {
        Assert.Equal(bikeKey, await SendNaviIconAsync(semanticIcon));
    }

    [Theory]
    [InlineData(true, "roundabout-right-2")]  // counter-clockwise
    [InlineData(false, "roundabout-left-2")]  // clockwise (UK, IE, …)
    public async Task Navi_RoundaboutFollowsTheDrivingSide(bool driveOnRight, string bikeKey)
    {
        Assert.Equal(bikeKey, await SendNaviIconAsync(NavigationIconMapper.IconRoundabout, roundaboutExitCount: 2, driveOnRight));
    }

    /// <summary>Sends one navigation update and returns the icon field of its NAVI frame.</summary>
    private static async Task<string> SendNaviIconAsync(string semanticIcon, int? roundaboutExitCount = null, bool driveOnRight = true)
    {
        FakeSession session = new();
        await new MvAgustaBlePlugin().SendNavigationUpdateAsync(session, new NavigationUpdateInput
        {
            ManeuverIcon = semanticIcon,
            RoundaboutExitCount = roundaboutExitCount,
            DriveOnRight = driveOnRight,
            InstructionText = "",
            StreetName = "",
            DistanceToTurnM = 100,
            SpeedKmh = 50,
            RemainingDistanceKm = 10,
            RemainingTimeMin = 12,
            CurrentManeuverIndex = 1,
            TotalManeuvers = 5
        });

        string[] navi = Encoding.UTF8.GetString(session.Writes[0].Data).Trim('\r').Split('\u001E');
        Assert.Equal("NAVI", navi[0]);
        return navi[1];
    }

    private sealed class FakeSession : IBleConnectedDevice
    {
        public sealed record Write(Guid Service, string Characteristic, byte[] Data, bool WithResponse);

        public List<Write> Writes { get; } = [];
        public Dictionary<(Guid, string), Action<byte[]>> Subscriptions { get; } = [];

        public string DeviceId => "test-device";

        public Task WriteAsync(Guid serviceUuid, string characteristicUuid, byte[] data, bool withResponse)
        {
            Writes.Add(new Write(serviceUuid, characteristicUuid, data, withResponse));
            return Task.CompletedTask;
        }

        public void Subscribe(Guid serviceUuid, string characteristicUuid, Action<byte[]> onData) =>
            Subscriptions[(serviceUuid, characteristicUuid)] = onData;
    }
}
