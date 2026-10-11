using Microsoft.AspNetCore.Components;
using MudBlazor;
using VegaBridgeApp.Models.Navigation;

namespace VegaBridgeApp.Components;

public partial class NavigationHud : ComponentBase
{
    // The NavigationService ignores fixes worse than this (tunnel, urban
    // canyon) – the HUD tells why it stopped updating.
    private const double GpsIgnoredAccuracyM = 40;

    /// <summary>Maneuver shown as the next action; null before the first one.</summary>
    [Parameter] public NavigationManeuverInfo? Maneuver { get; set; }

    /// <summary>Latest position status; null before the first GPS fix.</summary>
    [Parameter] public NavigationStatus? Status { get; set; }

    /// <summary>Route length at navigation start – the progress bar measures against it.</summary>
    [Parameter] public double TotalDistanceKm { get; set; }

    [Parameter] public bool ClosureCheckRunning { get; set; }

    /// <summary>Current GPS accuracy in meters (0 = unknown).</summary>
    [Parameter] public double GpsAccuracyM { get; set; }

    private bool GpsWeak => GpsAccuracyM > GpsIgnoredAccuracyM;

    /// <summary>No GPS (tunnel): the position is estimated along the route.</summary>
    private bool GpsEstimated => Status?.IsEstimated == true;

    /// <summary>Progress along the route by distance, 0-100 (a reroute does not reset it).</summary>
    private double ProgressPercent => Status == null || TotalDistanceKm <= 0
        ? 0
        : Math.Clamp((1 - Status.RemainingDistanceKm / TotalDistanceKm) * 100, 0, 100);

    private string RoundaboutExitText => Maneuver?.RoundaboutExit is { } exit
        ? $"{exit}/{Maneuver.RoundaboutExitCount}"
        : $"{Maneuver?.RoundaboutExitCount}";

    private string TurnIcon => NavigationIconMapper.GetSemanticIcon(Maneuver?.ValhallaType ?? 0) switch
    {
        NavigationIconMapper.IconTurnLeft => Icons.Material.Filled.TurnLeft,
        NavigationIconMapper.IconTurnRight => Icons.Material.Filled.TurnRight,
        NavigationIconMapper.IconSlightLeft => Icons.Material.Filled.TurnSlightLeft,
        NavigationIconMapper.IconSlightRight => Icons.Material.Filled.TurnSlightRight,
        NavigationIconMapper.IconSharpLeft => Icons.Material.Filled.TurnSharpLeft,
        NavigationIconMapper.IconSharpRight => Icons.Material.Filled.TurnSharpRight,
        NavigationIconMapper.IconUTurnLeft => Icons.Material.Filled.UTurnLeft,
        NavigationIconMapper.IconUTurnRight => Icons.Material.Filled.UTurnRight,
        NavigationIconMapper.IconExitLeft => Icons.Material.Filled.RampLeft,
        NavigationIconMapper.IconExitRight => Icons.Material.Filled.RampRight,
        NavigationIconMapper.IconStraight => Icons.Material.Filled.Straight,
        NavigationIconMapper.IconFinish => Icons.Material.Filled.Flag,
        NavigationIconMapper.IconRoundabout => Icons.Material.Filled.RotateRight,
        _ => Icons.Material.Filled.Straight
    };

    /// <summary>Fallback text when the maneuver has no instruction.</summary>
    private string TurnLabel => NavigationIconMapper.GetSemanticIcon(Maneuver?.ValhallaType ?? 0) switch
    {
        NavigationIconMapper.IconTurnLeft => L["TurnLeft"],
        NavigationIconMapper.IconTurnRight => L["TurnRight"],
        NavigationIconMapper.IconSlightLeft => L["TurnSlightLeft"],
        NavigationIconMapper.IconSlightRight => L["TurnSlightRight"],
        NavigationIconMapper.IconSharpLeft => L["TurnSharpLeft"],
        NavigationIconMapper.IconSharpRight => L["TurnSharpRight"],
        NavigationIconMapper.IconUTurnLeft or NavigationIconMapper.IconUTurnRight => L["UTurn"],
        NavigationIconMapper.IconExitLeft => L["ExitLeft"],
        NavigationIconMapper.IconExitRight => L["ExitRight"],
        NavigationIconMapper.IconStraight => L["Straight"],
        NavigationIconMapper.IconFinish => L["Arrival"],
        NavigationIconMapper.IconRoundabout => L["Roundabout"],
        _ => L["Straight"]
    };
}
