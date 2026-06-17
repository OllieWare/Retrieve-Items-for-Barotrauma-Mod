# Retrieve Items Order

This mod adds a new bot order called `Retrieve Items`.

## What It Does

When you issue the order to a bot, the bot will:

1. Leave the submarine and path to a docked non-player outpost.
2. Scan for loose items on the floor.
3. Pick up items until inventory space is effectively exhausted.
4. Return to the player submarine.
5. Store the loot using the same container-selection logic as the vanilla `Cleanup Items` order.

The implementation reuses Barotrauma's existing bot objectives for:

- outdoor pathfinding
- diving gear and oxygen management
- item pickup
- returning to the submarine
- vanilla cleanup-style item storage

## Compatibility

This mod is written to coexist with other order mods, including `Order to Rest`.

- It does not replace `Orders.xml` from another mod; it adds its own order prefab in a separate file.
- Its Harmony patch on `AIObjectiveManager.CreateObjective` is a postfix.
- It only creates an objective for the custom identifier `retrieveitems`.
- If vanilla or another mod has already created an objective for a different order, this mod leaves that result untouched.

## Required Setup

1. Install `LuaCs For Barotrauma`.
2. Enable LuaCs C# scripting in the LuaCs settings menu.
3. Place this mod folder in `Barotrauma/LocalMods/`.
4. Enable the mod in the content package list.

## Testing

1. Start or load a round where your submarine is docked to an abandoned outpost.
2. Select a bot crewmember.
3. Open the command wheel.
4. Choose `Retrieve Items`.
5. Watch the bot move into the outpost, collect floor loot, return, and store it.

## Notes

- Outpost detection prefers a docked non-player submarine whose name contains `abandoned`.
- If your campaign or custom map uses different naming, update `FindTargetLocation()` in `CSharp/Shared/RetrieveItemsOrderRules.cs`.
- The bot only targets loose items with no parent inventory.
- The bot aborts when critically injured.
- Item placement on return now uses `AIObjectiveCleanupItems`, so retrieved items are sorted the same way vanilla cleanup tries to sort loose items on the submarine.
- The bot depends on vanilla pathing and safety evaluation, so unreachable or very dangerous areas may still be skipped.

## Source Layout

All C# source lives under `CSharp/Shared/` and shares the `RetrieveItemsOrderMod` namespace. LuaCs loads `.cs` files by convention, so no manifest entry is needed.

| File | Purpose |
|------|---------|
| `RetrieveItemsPlugin.cs` | `IAssemblyPlugin` entry point: Harmony setup, patch orchestration, dispose |
| `RetrieveItemsIds.cs` | Static identifier constants (order identifiers, dialog keys, tags) |
| `RetrieveItemsOrderRules.cs` | Shared rules: target location resolution, container marking, mark relay, safety checks |
| `AIObjectiveRetrieveItems.cs` | Outpost retrieval objective: searching, returning, depositing |
| `AIObjectiveRetrieveWreckItems.cs` | Wreck retrieval objective: state machine, airlock, diving gear, inventory |
| `AIObjectiveRetrieveWreckItems.OpenWater.cs` | Partial class: open-water A* pathfinding and physics navigation |
| `Patches/CreateObjectivePatch.cs` | Harmony patch routing custom orders to the mod's objective classes |
| `Patches/SetOrderSpeechPatch.cs` | Harmony patch for mark-container speech and dismissal handling |
| `Patches/MarkContainerChatRelayPatch.cs` | Harmony patch consuming hidden mark-relay chat messages |
| `Patches/CrewManagerContextualOrderPatch.cs` | Harmony patch adding "mark container" button to the crew order UI |
| `Patches/MarkedContainerHudPatchShared.cs` | Harmony patch drawing marked-container icons on the HUD |
