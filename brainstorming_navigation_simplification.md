# Brainstorming: Navigation Simplification

## User Intent
The user believes the current navigation/routing implementation is unnecessarily complex because Valhalla already provides turn-by-turn instructions.

## Current Complexity (The "Hard Way")
1. **Manual Route Snapping**: `FindNearestRouteIndexWithLookahead` implements custom logic to find the nearest point on the polyline.
2. **Manual Distance Summing**: `CalculateDistanceToNextTurn` and `CalculateRemaining` iterate through coordinates every GPS tick to sum distances.
3. **Maneuver Look-ahead**: `GetDisplayManeuverIndex` implements logic to skip "straight" maneuvers to show the next relevant turn.
4. **State Coordination**: `NavigationService` -> `BleNavigationCoordinator` -> `MvAgustaBlePlugin` (3 layers).

## Potential Simplifications (The "Lazy/Efficient Way")

### 1. Distance Calculation (Performance/Code)
- **Current**: Loop over points every tick.
- **Lazy**: Pre-calculate a `double[] cumulativeDistances` array during `StartNavigation`.
- **Result**: Distance to turn = `cumulativeDistances[targetIndex] - cumulativeDistances[currentRouteIndex]`. O(1) instead of O(N).

### 2. Maneuver Selection
- **Current**: Complex `GetDisplayManeuverIndex` logic with `_relevantManeuverIndices`.
- **Lazy**: If Valhalla's maneuver list is already clean, just use the current one. If the bike's display can handle "straight" segments, we don't need to skip them.
- **Question**: Does the bike actually *require* us to skip straight segments? If the hardware just shows "Next turn: 500m", and the current maneuver is "Straight", showing "Straight 500m" might be fine, or just showing the *next* turn regardless of the current one.

### 3. Route Snapping
- **Current**: Custom `FindNearestRouteIndexWithLookahead`.
- **Lazy**: Is there a simpler way? Or is this necessary for accuracy? Probably necessary to avoid "jumping" to parallel roads, but maybe it can be simplified.

### 4. Architecture
- **Current**: `NavigationService` -> `BleNavigationCoordinator` -> `MvAgustaBlePlugin`.
- **Lazy**: The `BleNavigationCoordinator` is mostly a mapper. If we only support one bike for now, it's a lot of boilerplate. However, the goal was "herstellerneutrale" (manufacturer-neutral) design.
- **Ponytail approach**: If the mapping is simple, can we just have the Plugin handle it? (Though the coordinator is already implemented, removing it might be more work than leaving it).

## Proposed "Ponytail" Plan
1. **Optimize Distance**: Implement cumulative distance array.
2. **Simplify Look-ahead**: Review if `_relevantManeuverIndices` is actually adding value or just complexity.
3. **Streamline Data Flow**: Ensure `NavigationService` just passes Valhalla data and the `MvAgustaBlePlugin` does the final mapping.

## Verification
- Verify distance updates are still accurate.
- Verify "Next Turn" display behaves as expected on the bike.
