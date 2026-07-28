# Off-Route Detection + Rerouting — Plan

## NavigationService changes
- OffRouteDetected event (lat, lon, distanceMeters)
- Reroute() method to replace route mid-navigation
- _isOffRoute tracking to avoid event spam
- FindNearestRouteIndex returns (index, distanceMeters)
- Threshold 50m

## Map.razor.cs changes
- Subscribe to OffRouteDetected
- RerouteAsync(): Valhalla API call from GPS pos → destination
- ShowRouteOnMap + NavService.Reroute
