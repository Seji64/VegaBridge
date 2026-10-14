using VegaBridgeApp.Models.Navigation;
using Xunit;

namespace VegaBridgeApp.Tests;

/// <summary>Valhalla maneuver type → semantic icon key shared by the UI and the plugins.</summary>
public class NavigationIconMapperTests
{
    [Theory]
    [InlineData(23, NavigationIconMapper.IconKeepRight)]   // kStayRight
    [InlineData(24, NavigationIconMapper.IconKeepLeft)]    // kStayLeft
    [InlineData(18, NavigationIconMapper.IconRampRight)]   // kRampRight
    [InlineData(19, NavigationIconMapper.IconRampLeft)]    // kRampLeft
    [InlineData(20, NavigationIconMapper.IconExitRight)]   // kExitRight
    [InlineData(21, NavigationIconMapper.IconExitLeft)]    // kExitLeft
    [InlineData(37, NavigationIconMapper.IconMergeRight)]  // kMergeRight
    [InlineData(38, NavigationIconMapper.IconMergeLeft)]   // kMergeLeft
    [InlineData(28, NavigationIconMapper.IconFerry)]       // kFerryEnter
    [InlineData(29, NavigationIconMapper.IconFerry)]       // kFerryExit
    [InlineData(9, NavigationIconMapper.IconSlightRight)]  // kSlightRight
    [InlineData(26, NavigationIconMapper.IconRoundabout)]  // kRoundaboutEnter
    [InlineData(99, NavigationIconMapper.IconStraight)]    // unknown type
    public void ValhallaType_KeepsWhatValhallaDistinguishes(int valhallaType, string expected)
    {
        Assert.Equal(expected, NavigationIconMapper.GetSemanticIcon(valhallaType));
    }

    [Theory]
    [InlineData(4)] // kDestination
    [InlineData(5)] // kDestinationRight
    [InlineData(6)] // kDestinationLeft
    public void Arrival_IsAWaypointUntilTheLastManeuver(int valhallaType)
    {
        NavigationManeuverInfo waypoint = new() { ValhallaType = valhallaType, Index = 3, Total = 10 };
        NavigationManeuverInfo destination = new() { ValhallaType = valhallaType, Index = 9, Total = 10 };

        Assert.Equal(NavigationIconMapper.IconWaypoint, NavigationIconMapper.GetSemanticIcon(waypoint));
        Assert.Equal(NavigationIconMapper.IconFinish, NavigationIconMapper.GetSemanticIcon(destination));
    }
}
