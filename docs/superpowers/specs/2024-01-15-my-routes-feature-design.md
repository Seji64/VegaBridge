# My Routes Feature Design

## Overview
This document describes the design for the "My Routes" feature in the VegaBridge app. This feature will replace the current Home page placeholder with a full-featured routes as JSON and supports GPX import.
## Core Requirements

1. Replace the current Home page with route" data structure for routes that include routes with GPX format

## Architecture Overview

### Components
- **MyRou**der - A page for displaying, managing, and interacting with saved routes
- **RouteService** - Service handling route persistance storage operations (save, load, delete, list)
- **GpxService** - Service handling GPX import/export functionality
- **RouteModel** - Domain model representing a saved route
- **MyRou.razor** - Main component implementing the UI

### Data Flow
1. User navigates to My Routes page (`/my-routes`)
2. RouteService loads all saved routes from file storage
3. MyRou.razor displays routes in a list/grid format
4. User can:
   - Tap a route to view details
   - Navigate to map with pre-loaded route
   - Delete a route
   - Import GPX file
   - Export route as GPX
5. All changes are persisted immediately via RouteService

### Storage Strategy
- Each route saved as individual JSON file in `FileSystem.AppDataDirectory/routes/`
- Filename: `{routeId}.json` where routeId is a GUID
- This approach provides:
  - Easy backup/migration (copy folder)
  - Simple deletion (remove file)
  - Human-readable format for debugging
  - No database dependencies

## Detailed Design

### RouteModel
```csharp
public class SavedRoute
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Route data from Valhalla
    public ValhallaRouteResponse? RouteResponse { get; set; }
    
    // Calculated properties for display
    public double DistanceKm => RouteResponse?.Trip?.Summary?.Distance ?? 0;
    public double TimeMin => (RouteResponse?.Trip?.Summary?.Time ?? 0) / 60;
}
```

### RouteService
Handles file-based persistence:
- `Task<List<SavedRoute>> GetAllRoutesAsync()`
- `Task<SavedRoute?> GetRouteByIdAsync(string id)`
- `Task SaveRouteAsync(SavedRoute route)`
- `Task DeleteRouteAsync(string id)`
- `Task<bool> RouteExistsAsync(string id)`

### GpxService
Handles GPX format conversion:
- `Task<SavedRoute?> ImportGpxAsync(Stream gpxStream)`
- `Task<Stream> ExportGpxAsync(SavedRoute route)`
- Uses standard GPX 1.1 schema with `<trk>` and `<trkseg>` elements

### MyRou.razor UI (MudBlazor)

All UI elements on the My Routes page will be built using MudBlazor components to keep a consistent look-and-feel across the app. The page will use:
- **MudAppBar** for the top toolbar (New Route, Import GPX, Refresh actions).
- **MudCard** (or **MudTable** for list view) to display each saved route with its name, distance, time, and creation date.
- **MudIconButton** for per‑route actions (Navigate, Delete, Export).
- **MudDialog** for import/export confirmations and progress indicators.
- **MudAlert** for empty‑state and error messages.

This ensures the UI matches the rest of the app, which already uses MudBlazor for dialogs, alerts, and layout.

### MyRou.razor UI
- **Toolbar**: New Route (placeholder), Import GPX, Refresh
- **Main Area**: Grid/List of route cards showing:
  - Route name
  - Distance and time
  - Creation date
  - Action buttons (Navigate, Delete, Export)
- **Empty State**: Friendly message when no routes saved
- **Dialogs**: For import/export progress and confirmations
- **Toolbar**: New Route (placeholder), Import GPX, Refresh
- **Main Area**: Grid/List of route cards showing:
  - Route name
  - Distance and time
  - Creation date
  - Action buttons (Navigate, Delete, Export)
- **Empty State**: Friendly message when no routes saved
- **Dialogs**: For import/export progress and confirmations

## Integration Points
1. **Navigation**: From Map page, after calculating route, offer to save it
2. **Deep Linking**: Route ID can be passed via query param to load specific route
3. **Maps Integration**: Selected route can be sent to Map page for display/navigation

## Error Handling & Validation
- File I/O errors handled gracefully with user feedback
- GPX validation during import (malformed XML caught)
- Duplicate prevention (though GUIDs make collisions unlikely)
- Storage space monitoring (warn if low space)

## Security Considerations
- No sensitive data stored in routes (just GPS coordinates and routing metadata)
- Files stored in app sandbox, not accessible to other apps
- GPX files scanned for basic XML safety (no script injection concerns)

## Testing Considerations
- Unit tests for RouteService (mock file system)
- Unit tests for GpxService (sample GPX files)
- Component tests for MyRou.razor (using bUnit)
- Integration tests for save/load cycles

## Future Enhancements
- Route categorization/tagging
- Route sharing via export/import
- Route editing (waypoint modification)
- Route statistics (elevation, etc.)
- Cloud sync capability

## Open Questions
1. Should we auto-save routes from Map page after calculation?
2. What fields should be editable for saved routes?
3. Should we support route reorganization (folders, tags)?