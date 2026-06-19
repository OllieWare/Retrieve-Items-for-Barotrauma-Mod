using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace RetrieveItemsOrderMod
{
    internal sealed partial class AIObjectiveRetrieveWreckItems : AIObjective
    {
        private enum WreckRetrieveState
        {
            Searching,
            Preparing,
            Traveling,
            Retrieving,
            Returning,
            Depositing,
            Finished
        }

        private enum WreckTravelPhase
        {
            ToAirlock,
            ExitingAirlock,
            OpenWater
        }

        private const float StatusCooldown = 5.0f;
        private const float StuckDistanceThreshold = 50.0f;
        private const float StuckTimeout = 8.0f;
        private const float PreTripOxygenRatio = 0.90f;
        private const float EmergencyOxygenRatio = 0.30f;
        private const float SearchRadius = 20000.0f;

        private readonly Order sourceOrder;
        private readonly HashSet<Item> initialInventoryItems = new HashSet<Item>();
        private readonly HashSet<Item> ignoredItems = new HashSet<Item>();
        private readonly Identifier[] portableContainerLootTags =
        {
            "crate".ToIdentifier(),
            "ammobox".ToIdentifier(),
            "mobilecontainer".ToIdentifier(),
            "artifactcontainer".ToIdentifier()
        };
        private readonly Identifier divingTag = "diving".ToIdentifier();
        private readonly Identifier oxygenTankContainerTag = "oxygentankcontainer".ToIdentifier();
        private readonly Identifier oxygenTankRefillerTag = "oxygentankrefiller".ToIdentifier();
        private readonly Identifier deepDivingTag = "deepdiving".ToIdentifier();

        private WreckRetrieveState state = WreckRetrieveState.Searching;
        private Submarine homeSubmarine;
        private AIObjective currentSubObjective;
        private SteeringManager openWaterSteering;
        private Item currentTargetItem;
        private Item pendingOxygenTank;
        private Hull exitAirlockHull;
        private Gap exitAirlockGap;
        private WreckTravelPhase travelPhase = WreckTravelPhase.ToAirlock;
        private bool exitAirlockDoorCommanded;
        private float statusTimer;
        private float stuckTimer;
        private float openWaterRepathTimer;
        private float openWaterProgressTimer;
        private float openWaterMovementLogTimer;
        private float openWaterObstacleLogTimer;
        private float openWaterLastDistance = float.MaxValue;
        private int openWaterLastObstacleCount = -1;
        private List<Rectangle> openWaterCachedObstacles;
        private int openWaterFailedRepathCount;
        private int openWaterGiveUpCount;
        private float airlockCycleTimer;
        private bool airlockCycleClosingPhase;
        private float airlockCycleCooldown;
        private float airlockExitWaitTimer;
        private Vector2 lastWorldPosition;
        private int lastCarriedCount;
        private bool usingOpenWaterFallback;
        private List<Vector2> openWaterPath = new List<Vector2>();
        private int openWaterPathIndex;
        private Vector2 openWaterPathGoal;

        public override Identifier Identifier { get; set; } = RetrieveItemsIds.WreckOrderIdentifier;
        public override string DebugTag => $"{Identifier} ({state})";
        public override bool AllowOutsideSubmarine => true;
        public override bool AllowInFriendlySubs => true;
        public override bool AllowInAnySub => true;
        public override bool KeepDivingGearOn => state != WreckRetrieveState.Finished || !IsSafeToUnequipDivingGear();

        public AIObjectiveRetrieveWreckItems(Character character, AIObjectiveManager objectiveManager, Order order, float priorityModifier = 1.0f)
            : base(character, objectiveManager, priorityModifier, RetrieveItemsIds.WreckOrderIdentifier)
        {
            sourceOrder = order;
            lastWorldPosition = character.WorldPosition;
            CaptureInitialInventoryItems();
        }

        public override bool CheckObjectiveState()
        {
            return IsCompleted;
        }

        public override float GetPriority()
        {
            if (character.IsDead)
            {
                Priority = 0.0f;
                Abandon = !objectiveManager.IsOrder(this);
                return Priority;
            }

            if (state == WreckRetrieveState.Depositing && CountCarriedLoot() > 0)
            {
                Priority = Math.Max(objectiveManager.GetOrderPriority(this), objectiveManager.GetCurrentPriority() + 10.0f);
                return Priority;
            }

            if (state == WreckRetrieveState.Traveling ||
                state == WreckRetrieveState.Retrieving ||
                state == WreckRetrieveState.Returning)
            {
                Priority = Math.Max(objectiveManager.GetOrderPriority(this), objectiveManager.GetCurrentPriority() + 10.0f);
                return Priority;
            }

            Priority = objectiveManager.IsOrder(this) ? objectiveManager.GetOrderPriority(this) : 10.0f;
            return Priority;
        }

        public override void Act(float deltaTime)
        {
            statusTimer -= deltaTime;
            UpdateStuckTimer(deltaTime);

            homeSubmarine ??= RetrieveItemsOrderRules.ResolveHomeSubmarine(character);
            if (homeSubmarine == null)
            {
                Speak("I can't find the submarine to return to.", RetrieveItemsIds.NoTargetDialog, 2.0f, force: true);
                Abandon = true;
                return;
            }

            if (ShouldAbortForInjury())
            {
                if (CountCarriedLoot() > 0 && state != WreckRetrieveState.Returning && state != WreckRetrieveState.Depositing)
                {
                    ClearSubObjective();
                    BeginReturning();
                    return;
                }

                Abandon = true;
                return;
            }

            if ((state == WreckRetrieveState.Traveling || state == WreckRetrieveState.Retrieving) &&
                GetActiveOxygenRatio() < EmergencyOxygenRatio)
            {
                ClearSubObjective();
                BeginReturning();
                return;
            }

            switch (state)
            {
                case WreckRetrieveState.Searching:
                    UpdateSearching();
                    break;
                case WreckRetrieveState.Preparing:
                    UpdatePreparing();
                    break;
                case WreckRetrieveState.Traveling:
                    UpdateTraveling(deltaTime);
                    break;
                case WreckRetrieveState.Retrieving:
                    UpdateRetrieving();
                    break;
                case WreckRetrieveState.Returning:
                    UpdateReturning();
                    break;
                case WreckRetrieveState.Depositing:
                    UpdateDepositing(deltaTime);
                    break;
                case WreckRetrieveState.Finished:
                    UpdateFinished();
                    break;
            }
        }

        private void UpdateSearching()
        {
            Speak("Searching for marked wreck salvage...", "retrievewreckitems.searching".ToIdentifier(), StatusCooldown);
            if (CountCarriedLoot() > 0)
            {
                BeginReturning();
                return;
            }

            currentTargetItem = FindNextMarkedWreckLoot();
            if (currentTargetItem == null)
            {
                Speak("I can't find any marked wreck salvage.", "retrievewreckitems.abort.notarget".ToIdentifier(), 2.0f, force: true);
                UnequipDivingGearIfIdle();
                state = WreckRetrieveState.Finished;
                return;
            }

            state = WreckRetrieveState.Preparing;
            statusTimer = 0.0f;
            ResetStuckTracking();
        }

        private void UpdatePreparing()
        {
            Speak("Preparing diving gear.", "retrievewreckitems.preparing".ToIdentifier(), StatusCooldown);

            if (currentTargetItem == null || currentTargetItem.Removed)
            {
                state = WreckRetrieveState.Searching;
                return;
            }

            if (!IsOnHomeSubmarine())
            {
                BeginReturning();
                return;
            }

            if (IsSubObjectiveFinished())
            {
                ClearSubObjective();
            }

            if (IsStuckOnCurrentSubObjective())
            {
                ClearSubObjective();
                Speak("I need diving gear before leaving the submarine.", "retrievewreckitems.abort.nogear".ToIdentifier(), 2.0f, force: true);
                Abandon = true;
                return;
            }

            if (IsSubObjectiveActive())
            {
                return;
            }

            Item divingGear = GetEquippedDivingGear();
            if (divingGear == null)
            {
                divingGear = FindAvailableDivingGear();
                if (divingGear == null)
                {
                    Speak("I need diving gear before leaving the submarine.", "retrievewreckitems.abort.nogear".ToIdentifier(), 2.0f, force: true);
                    Abandon = true;
                    return;
                }

                currentSubObjective = new AIObjectiveGetItem(character, divingGear, objectiveManager, equip: true)
                {
                    MustBeSpecificItem = true,
                    Wear = true,
                    AllowStealing = false
                };
                AddSubObjective(currentSubObjective);
                return;
            }

            if (GetActiveOxygenRatio(divingGear) >= PreTripOxygenRatio)
            {
                BeginTravelingToWreckTarget();
                return;
            }

            if (pendingOxygenTank != null)
            {
                if (IsItemInCharacterInventory(pendingOxygenTank) || TryInstallOxygenTank(divingGear, pendingOxygenTank))
                {
                    TryInstallOxygenTank(divingGear, pendingOxygenTank);
                    pendingOxygenTank = null;
                    if (GetActiveOxygenRatio(divingGear) >= PreTripOxygenRatio)
                    {
                        BeginTravelingToWreckTarget();
                        return;
                    }
                }

                pendingOxygenTank = null;
            }

            Item fullTank = FindFullOxygenTank();
            if (fullTank != null)
            {
                if (IsItemInCharacterInventory(fullTank) || TryInstallOxygenTank(divingGear, fullTank))
                {
                    TryInstallOxygenTank(divingGear, fullTank);
                    if (GetActiveOxygenRatio(divingGear) >= PreTripOxygenRatio)
                    {
                        BeginTravelingToWreckTarget();
                        return;
                    }
                }

                pendingOxygenTank = fullTank;
                currentSubObjective = new AIObjectiveGetItem(character, fullTank, objectiveManager, equip: false)
                {
                    MustBeSpecificItem = true,
                    Wear = false,
                    AllowStealing = false
                };
                AddSubObjective(currentSubObjective);
                return;
            }

            Speak("I need a full oxygen tank before leaving the submarine.", "retrievewreckitems.abort.nooxygen".ToIdentifier(), 2.0f, force: true);
            Abandon = true;
        }

        private void UpdateTraveling(float deltaTime)
        {
            Speak("Moving to marked salvage.", "retrievewreckitems.traveling".ToIdentifier(), StatusCooldown);
            if (!IsValidWreckLoot(currentTargetItem))
            {
                ignoredItems.Add(currentTargetItem);
                ClearTarget();
                StopOpenWaterFallback();
                state = WreckRetrieveState.Searching;
                return;
            }

            if (travelPhase == WreckTravelPhase.ToAirlock)
            {
                UpdateTravelingToAirlock();
                return;
            }

            if (travelPhase == WreckTravelPhase.ExitingAirlock)
            {
                UpdateExitingAirlock(deltaTime);
                return;
            }

            if (!usingOpenWaterFallback && character.CurrentHull != null)
            {
                ReleaseOpenWaterMovementControl();
            }

            if (usingOpenWaterFallback || ShouldUseOpenWaterFallback())
            {
                if (!usingOpenWaterFallback)
                {
                    ClearSubObjective();
                    StartOpenWaterFallback();
                }

                if (stuckTimer >= StuckTimeout)
                {
                    float distToTarget = Vector2.Distance(character.WorldPosition, currentTargetItem?.WorldPosition ?? character.WorldPosition);
                    bool tryDirect = (distToTarget <= OpenWaterDirectGoalThreshold || openWaterFailedRepathCount >= 2) &&
                                     currentTargetItem != null && !currentTargetItem.Removed;
                    if (tryDirect)
                    {
                        Vector2 directMovement = currentTargetItem.WorldPosition - character.WorldPosition;
                        if (directMovement.LengthSquared() > 1.0f)
                        {
                            Vector2 normalized = Vector2.Normalize(directMovement);
                            List<Rectangle> obstacles = GetOpenWaterObstacles();
                            Vector2 bestDir = Vector2.Zero;
                            float bestScore = float.MinValue;
                            Vector2 perpA = new Vector2(-normalized.Y, normalized.X);
                            Vector2 perpB = new Vector2(normalized.Y, -normalized.X);
                            Vector2[] candidates = { normalized, perpA, perpB, -perpA, -perpB, -normalized };
                            foreach (Vector2 dir in candidates)
                            {
                                if (OpenWaterSegmentBlocked(character.WorldPosition, character.WorldPosition + dir * 100.0f, obstacles))
                                {
                                    continue;
                                }

                                float score = Vector2.Dot(dir, normalized);
                                if (score > bestScore)
                                {
                                    bestScore = score;
                                    bestDir = dir;
                                }
                            }

                            if (bestDir.LengthSquared() < 0.01f)
                            {
                                openWaterFailedRepathCount++;
                                openWaterPath.Clear();
                                openWaterPathIndex = 0;
                                openWaterRepathTimer = 0.0f;
                                ResetStuckTracking();
                                LuaCsLogger.Log($"[RetrieveItemsOrder] All directions blocked for {character.Name}; releasing path (failedRepaths={openWaterFailedRepathCount})");
                                return;
                            }

                            character.OverrideMovement = bestDir;
                            try
                            {
                                SteeringManager?.SteeringManual(deltaTime, bestDir);
                            }
                            catch { }
                            character.SetInput(InputType.Left, bestDir.X < -0.15f, false);
                            character.SetInput(InputType.Right, bestDir.X > 0.15f, false);
                            character.SetInput(InputType.Up, bestDir.Y > 0.15f, false);
                            character.SetInput(InputType.Down, bestDir.Y < -0.15f, false);
                            LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water stuck for {character.Name}; wall-slide (failedRepaths={openWaterFailedRepathCount}), dir=({bestDir.X:0.00},{bestDir.Y:0.00}), distance={distToTarget:0}");
                            return;
                        }
                    }

                    openWaterFailedRepathCount++;
                    openWaterPath.Clear();
                    openWaterPathIndex = 0;
                    openWaterRepathTimer = 0.0f;
                    ResetStuckTracking();
                    LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water stuck for {character.Name}; forcing repath (failedRepaths={openWaterFailedRepathCount}), distance={distToTarget:0}");
                    return;
                }

                if (UpdateOpenWaterNavigation(deltaTime, currentTargetItem, OpenWaterCloseEnough))
                {
                    StopOpenWaterFallback();
                    state = WreckRetrieveState.Retrieving;
                    statusTimer = 0.0f;
                    ResetStuckTracking();
                }
                return;
            }

            if (currentSubObjective?.IsCompleted == true)
            {
                ClearSubObjective();
                StopOpenWaterFallback();
                state = WreckRetrieveState.Retrieving;
                statusTimer = 0.0f;
                ResetStuckTracking();
                return;
            }

            if (currentSubObjective?.Abandon == true)
            {
                ClearSubObjective();
                if (IsCloseToCurrentTarget(250.0f))
                {
                    StopOpenWaterFallback();
                    state = WreckRetrieveState.Retrieving;
                    statusTimer = 0.0f;
                    ResetStuckTracking();
                    return;
                }

                if (ShouldUseOpenWaterFallback())
                {
                    StartOpenWaterFallback();
                    return;
                }

                ResetStuckTracking();
                return;
            }

            if (IsStuckOnCurrentSubObjective())
            {
                if (ShouldUseOpenWaterFallback())
                {
                    ClearSubObjective();
                    StartOpenWaterFallback();
                    return;
                }

                ClearSubObjective();
                ResetStuckTracking();
                return;
            }

            if (!IsSubObjectiveActive())
            {
                currentSubObjective = new AIObjectiveGoTo(currentTargetItem, character, objectiveManager, repeat: false, getDivingGearIfNeeded: false, priorityModifier: 1.0f, closeEnough: 250.0f)
                {
                    AllowGoingOutside = true,
                    SpeakIfFails = false
                };
                AddSubObjective(currentSubObjective);
            }
        }

        private void BeginTravelingToWreckTarget()
        {
            ClearSubObjective();
            StopOpenWaterFallback();
            exitAirlockHull = null;
            exitAirlockGap = null;
            exitAirlockDoorCommanded = false;
            airlockCycleTimer = 0.0f;
            airlockCycleClosingPhase = false;
            airlockCycleCooldown = 0.0f;
            airlockExitWaitTimer = 0.0f;
            travelPhase = WreckTravelPhase.ToAirlock;
            state = WreckRetrieveState.Traveling;
            statusTimer = 0.0f;
            ResetStuckTracking();
        }

        private void UpdateTravelingToAirlock()
        {
            ReleaseOpenWaterMovementControl();
            if (ShouldUseOpenWaterFallback())
            {
                ClearSubObjective();
                travelPhase = WreckTravelPhase.OpenWater;
                StartOpenWaterFallback();
                return;
            }

            if (exitAirlockHull == null || exitAirlockGap == null)
            {
                ResolveExitAirlock();
            }

            if (exitAirlockHull == null || exitAirlockGap == null)
            {
                Speak("I can't find a usable airlock.", "retrievewreckitems.abort.noairlock".ToIdentifier(), 2.0f, force: true);
                Abandon = true;
                return;
            }

            if (character.CurrentHull == exitAirlockHull || IsCharacterInsideHullBounds(exitAirlockHull))
            {
                ClearSubObjective();
                travelPhase = WreckTravelPhase.ExitingAirlock;
                ResetStuckTracking();
                LuaCsLogger.Log($"[RetrieveItemsOrder] Wreck retrieval reached exit airlock for {character.Name}: hull={GetHullName(exitAirlockHull)}");
                return;
            }

            if (currentSubObjective?.Abandon == true || IsStuckOnCurrentSubObjective())
            {
                ClearSubObjective();
                ResetStuckTracking();
                return;
            }

            if (!IsSubObjectiveActive())
            {
                currentSubObjective = CreateGoToHullObjective(exitAirlockHull, closeEnough: 80.0f);
                if (currentSubObjective == null)
                {
                    travelPhase = WreckTravelPhase.ExitingAirlock;
                    ResetStuckTracking();
                    return;
                }

                AddSubObjective(currentSubObjective);
                LuaCsLogger.Log($"[RetrieveItemsOrder] Wreck retrieval moving to exit airlock for {character.Name}: hull={GetHullName(exitAirlockHull)}");
            }
        }

        private void UpdateExitingAirlock(float deltaTime)
        {
            if (exitAirlockHull == null || exitAirlockGap == null)
            {
                travelPhase = WreckTravelPhase.ToAirlock;
                return;
            }

            if (character.CurrentHull != exitAirlockHull && !IsCharacterInsideHullBounds(exitAirlockHull))
            {
                ClearSubObjective();
                travelPhase = WreckTravelPhase.OpenWater;
                ReleaseExitAirlockDoorCommand();
                StartOpenWaterFallback();
                return;
            }

            if (!CloseInteriorAirlockDoors(exitAirlockHull, exitAirlockGap))
            {
                ReleaseOpenWaterMovementControl();
                return;
            }

            Door exitDoor = exitAirlockGap.ConnectedDoor;
            if (exitDoor != null && !exitDoor.IsOpen)
            {
                ToggleDoor(exitDoor, true);
                airlockExitWaitTimer = 0.0f;
            }

            if (exitDoor != null && exitDoor.IsOpen)
            {
                airlockExitWaitTimer += deltaTime;
                character.OverrideMovement = null;
                ClearOpenWaterMovementInputs();

                if (airlockExitWaitTimer > 2.0f)
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] Exit airlock door open but {character.Name} still inside after {airlockExitWaitTimer:0.0}s; transitioning to open water");
                    ClearSubObjective();
                    travelPhase = WreckTravelPhase.OpenWater;
                    ReleaseExitAirlockDoorCommand();
                    StartOpenWaterFallback();
                    return;
                }

                return;
            }

            ReleaseOpenWaterMovementControl();
        }

        private void ResolveExitAirlock()
        {
            exitAirlockHull = Hull.HullList
                .Where(hull => hull != null && hull.Submarine == homeSubmarine)
                .Where(IsUsableExitAirlockHull)
                .Select(hull => new { Hull = hull, Gap = FindExteriorGap(hull) })
                .Where(result => result.Gap != null)
                .OrderBy(result => Vector2.DistanceSquared(character.WorldPosition, GetHullCenter(result.Hull)))
                .Select(result =>
                {
                    exitAirlockGap = result.Gap;
                    return result.Hull;
                })
                .FirstOrDefault();

            if (exitAirlockHull != null)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Wreck retrieval selected exit airlock for {character.Name}: hull={GetHullName(exitAirlockHull)}, gap={exitAirlockGap?.Name ?? "<null>"}");
            }
        }

        private Gap FindExteriorGap(Hull hull)
        {
            return GetConnectedGaps(hull)
                .Where(gap => gap != null && gap.ConnectedDoor != null && GetOtherLinkedHull(gap, hull) == null)
                .OrderBy(gap => Vector2.DistanceSquared(GetGapCenter(gap), currentTargetItem?.WorldPosition ?? character.WorldPosition))
                .FirstOrDefault();
        }

        private IEnumerable<Gap> GetConnectedGaps(Hull hull)
        {
            if (hull == null)
            {
                return Enumerable.Empty<Gap>();
            }

            object gaps = AccessTools.Field(typeof(Hull), "ConnectedGaps")?.GetValue(hull);
            return gaps as IEnumerable<Gap> ?? Enumerable.Empty<Gap>();
        }

        private Hull GetOtherLinkedHull(Gap gap, Hull hull)
        {
            try
            {
                return gap?.GetOtherLinkedHull(hull);
            }
            catch
            {
                return null;
            }
        }

        private bool IsUsableExitAirlockHull(Hull hull)
        {
            if (hull == null)
            {
                return false;
            }

            if (hull.IsAirlock)
            {
                return true;
            }

            string hullName = GetHullName(hull).Trim();
            if (hullName.Equals("airlock", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private Vector2 GetHullCenter(Hull hull)
        {
            Rectangle rect = GetWorldRect(hull);
            return rect.Width > 0 && rect.Height > 0
                ? new Vector2(rect.Center.X, rect.Center.Y)
                : Vector2.Zero;
        }

        private Vector2 GetGapCenter(Gap gap)
        {
            if (gap?.ConnectedDoor?.Item != null)
            {
                return gap.ConnectedDoor.Item.WorldPosition;
            }

            Rectangle rect = gap?.Rect ?? Rectangle.Empty;
            if (rect.Width > 0 || rect.Height > 0)
            {
                return new Vector2(rect.Center.X, rect.Center.Y);
            }

            return Vector2.Zero;
        }

        private Vector2 GetExternalExitPoint(Hull hull, Gap gap)
        {
            Vector2 hullCenter = GetHullCenter(hull);
            Vector2 gapCenter = GetGapCenter(gap);
            Vector2 direction = gapCenter - hullCenter;
            if (direction.LengthSquared() < 1.0f)
            {
                direction = currentTargetItem != null ? currentTargetItem.WorldPosition - character.WorldPosition : Vector2.UnitX;
            }

            direction.Normalize();
            return gapCenter + (direction * 350.0f);
        }

        private void OpenExitAirlockDoor(Gap gap)
        {
            Door door = gap?.ConnectedDoor;
            if (door == null)
            {
                return;
            }

            door.BotsShouldKeepOpen = false;
            ToggleDoor(door, true);
            if (!exitAirlockDoorCommanded)
            {
                exitAirlockDoorCommanded = true;
                LuaCsLogger.Log($"[RetrieveItemsOrder] Wreck retrieval opened exit airlock door for {character.Name}: gap={gap.Name ?? "<unnamed>"}");
            }
        }

        private void ToggleDoor(Door door, bool open)
        {
            if (door == null)
            {
                return;
            }

            bool isAlreadyCorrectState = open ? door.IsOpen : !door.IsOpen;
            if (isAlreadyCorrectState)
            {
                return;
            }

            door.ToggleState(ActionType.OnUse, character);
        }

        private bool CloseInteriorAirlockDoors(Hull airlockHull, Gap exteriorGap)
        {
            bool allClosed = true;
            foreach (Gap gap in GetConnectedGaps(airlockHull))
            {
                if (gap == null || gap == exteriorGap || GetOtherLinkedHull(gap, airlockHull) == null)
                {
                    continue;
                }

                Door door = gap.ConnectedDoor;
                if (door == null)
                {
                    continue;
                }

                door.BotsShouldKeepOpen = false;
                if (door.IsOpen)
                {
                    ToggleDoor(door, false);
                    allClosed = false;
                }
            }

            return allClosed;
        }

        private void ReleaseExitAirlockDoorCommand()
        {
            Door door = exitAirlockGap?.ConnectedDoor;
            if (door != null)
            {
                door.BotsShouldKeepOpen = false;
                ToggleDoor(door, false);
            }

            exitAirlockDoorCommanded = false;
        }

        private void ApplyAirlockExitMovement(float deltaTime, Vector2 exitPoint)
        {
            Vector2 movement = exitPoint - character.WorldPosition;
            if (movement.LengthSquared() <= 1.0f)
            {
                ReleaseOpenWaterMovementControl();
                return;
            }

            Vector2 movementVector = Vector2.Normalize(movement);
            ApplyOpenWaterMovementInputs(deltaTime, movementVector);
        }

        private AIObjective CreateGoToHullObjective(Hull hull, float closeEnough)
        {
            if (hull == null)
            {
                return null;
            }

            try
            {
                object[] constructorArgs =
                {
                    hull,
                    character,
                    objectiveManager,
                    false,
                    false,
                    1.0f,
                    closeEnough
                };
                var constructor = typeof(AIObjectiveGoTo)
                    .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(ctor =>
                    {
                        var parameters = ctor.GetParameters();
                        return parameters.Length == 7 &&
                               parameters[0].ParameterType.IsAssignableFrom(hull.GetType()) &&
                               parameters[1].ParameterType == typeof(Character) &&
                               parameters[2].ParameterType == typeof(AIObjectiveManager) &&
                               parameters[3].ParameterType == typeof(bool) &&
                               parameters[4].ParameterType == typeof(bool) &&
                               parameters[5].ParameterType == typeof(float) &&
                               parameters[6].ParameterType == typeof(float);
                    });
                if (constructor?.Invoke(constructorArgs) is AIObjectiveGoTo objective)
                {
                    objective.AllowGoingOutside = false;
                    objective.SpeakIfFails = false;
                    return objective;
                }
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to create wreck airlock objective: {ex.Message}");
            }

            return null;
        }

        private void UpdateRetrieving()
        {
            Speak("Recovering marked salvage.", "retrievewreckitems.retrieving".ToIdentifier(), StatusCooldown);
            if (!IsValidWreckLoot(currentTargetItem))
            {
                ignoredItems.Add(currentTargetItem);
                ClearTarget();
                state = CountCarriedLoot() > 0 ? WreckRetrieveState.Returning : WreckRetrieveState.Searching;
                return;
            }

            if (IsSubObjectiveFinished())
            {
                ClearSubObjective();
                if (CountCarriedLoot() > 0)
                {
                    BeginReturning();
                    return;
                }

                ignoredItems.Add(currentTargetItem);
                ClearTarget();
                state = WreckRetrieveState.Searching;
                return;
            }

            if (IsStuckOnCurrentSubObjective())
            {
                if (CountCarriedLoot() > 0)
                {
                    BeginReturning();
                    return;
                }

                ignoredItems.Add(currentTargetItem);
                ClearSubObjective();
                ClearTarget();
                state = WreckRetrieveState.Searching;
                ResetStuckTracking();
                return;
            }

            if (TryDirectPickupWreckLoot())
            {
                BeginReturning();
                return;
            }

            if (!IsSubObjectiveActive())
            {
                lastCarriedCount = CountCarriedLoot();
                currentSubObjective = new AIObjectiveGetItem(character, currentTargetItem, objectiveManager, equip: false)
                {
                    MustBeSpecificItem = true,
                    Wear = false,
                    AllowStealing = true
                };
                AddSubObjective(currentSubObjective);
            }
        }

        private bool TryDirectPickupWreckLoot()
        {
            if (currentTargetItem == null ||
                currentTargetItem.Removed ||
                Vector2.DistanceSquared(character.WorldPosition, currentTargetItem.WorldPosition) > 300.0f * 300.0f)
            {
                return false;
            }

            int carriedBefore = CountCarriedLoot();
            bool picked = false;
            Pickable pickable = currentTargetItem.GetComponent<Pickable>();
            if (pickable != null)
            {
                picked = pickable.Pick(character);
            }

            if (!picked)
            {
                Holdable holdable = currentTargetItem.GetComponent<Holdable>();
                if (holdable != null)
                {
                    picked = holdable.Pick(character);
                }
            }

            if (!picked)
            {
                picked = currentTargetItem.TryInteract(character, false, false, false);
            }

            bool nowCarried =
                currentTargetItem.ParentInventory == character.Inventory ||
                currentTargetItem.Equipper == character ||
                CountCarriedLoot() > carriedBefore;

            if (picked || nowCarried)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Direct wreck pickup for {character.Name}: item={currentTargetItem.Name}, picked={picked}, carried={nowCarried}");
                return true;
            }

            return false;
        }

        private void UpdateReturning()
        {
            Speak("Returning with salvage.", "retrievewreckitems.returning".ToIdentifier(), StatusCooldown);
            if (HasReturnedHome())
            {
                CompleteWreckOrderBeforeDepositing();
                return;
            }

            if (IsSubObjectiveFinished())
            {
                ClearSubObjective();
                if (HasReturnedHome())
                {
                    CompleteWreckOrderBeforeDepositing();
                    return;
                }
            }

            if (IsStuckOnCurrentSubObjective())
            {
                ClearSubObjective();
                Abandon = true;
                return;
            }

            if (!IsSubObjectiveActive())
            {
                currentSubObjective = new AIObjectiveReturn(character, sourceOrder.OrderGiver, objectiveManager);
                AddSubObjective(currentSubObjective);
            }
        }

        private void CompleteWreckOrderBeforeDepositing()
        {
            ClearSubObjective();
            StopOpenWaterFallback();
            UnmarkRetrievedWreckTarget();
            state = WreckRetrieveState.Finished;
            statusTimer = 0.0f;
            ResetStuckTracking();
            IsCompleted = true;
            LuaCsLogger.Log($"[RetrieveItemsOrder] Wreck retrieval reached submarine for {character.Name}; completing before deposit for vanilla handoff test");
        }

        private void UnmarkRetrievedWreckTarget()
        {
            if (currentTargetItem == null ||
                currentTargetItem.Removed ||
                !RetrieveItemsOrderRules.IsMarkedContainer(currentTargetItem))
            {
                return;
            }

            RetrieveItemsOrderRules.SetMarkedContainerState(currentTargetItem, false);
            LuaCsLogger.Log($"[RetrieveItemsOrder] Unmarked retrieved wreck target for {character.Name}: {currentTargetItem.Name}");
        }

        private void UpdateDepositing(float deltaTime)
        {
            Speak("Depositing recovered salvage.", "retrievewreckitems.depositing".ToIdentifier(), StatusCooldown);

            if (CountCarriedLoot() <= 0)
            {
                Speak("Wreck salvage secured.", "retrievewreckitems.done".ToIdentifier(), 2.0f, force: true);
                ClearTarget();
                state = WreckRetrieveState.Searching;
                statusTimer = 0.0f;
                ResetStuckTracking();
                return;
            }

            if (IsSubObjectiveFinished())
            {
                ClearSubObjective();
            }

            if (IsStuckOnCurrentSubObjective())
            {
                if (!DropNextLootToSubFloor())
                {
                    Abandon = true;
                    return;
                }

                ClearSubObjective();
                ResetStuckTracking();
            }

            if (!IsSubObjectiveActive())
            {
                List<Item> carriedLoot = GetCarriedLoot().ToList();
                if (carriedLoot.Count == 0)
                {
                    state = WreckRetrieveState.Searching;
                    return;
                }

                lastCarriedCount = carriedLoot.Count;
                currentSubObjective = new AIObjectiveCleanupItem(carriedLoot.First(), character, objectiveManager, 1.0f);
                AddSubObjective(currentSubObjective);
                currentSubObjective.Act(deltaTime);
            }
        }

        private void UpdateFinished()
        {
            if (FindNextMarkedWreckLoot() != null)
            {
                state = WreckRetrieveState.Searching;
                statusTimer = 0.0f;
            }
        }

        private Item FindNextMarkedWreckLoot()
        {
            return RetrieveItemsOrderRules.GetMarkedContainers()
                .SelectMany(GetWreckLootCandidatesFromMarkedContainer)
                .Where(IsValidWreckLoot)
                .OrderBy(item => Vector2.DistanceSquared(character.WorldPosition, item.WorldPosition))
                .FirstOrDefault();
        }

        private IEnumerable<Item> GetWreckLootCandidatesFromMarkedContainer(Item container)
        {
            if (container == null || container.Removed)
            {
                yield break;
            }

            if (IsPortableContainerLoot(container))
            {
                yield return container;
                yield break;
            }

            ItemContainer itemContainer = container.GetComponent<ItemContainer>();
            if (itemContainer?.Inventory == null)
            {
                yield break;
            }

            foreach (Item item in GetCandidateLootItemsFromInventory(itemContainer.Inventory))
            {
                yield return item;
            }
        }

        private IEnumerable<Item> GetCandidateLootItemsFromInventory(object inventory)
        {
            foreach (Item item in GetDirectInventoryItems(inventory))
            {
                if (item == null || item.Removed)
                {
                    continue;
                }

                if (IsPortableContainerLoot(item))
                {
                    yield return item;
                    continue;
                }

                yield return item;

                ItemContainer nestedContainer = item.GetComponent<ItemContainer>();
                if (nestedContainer?.Inventory == null)
                {
                    continue;
                }

                foreach (Item nestedItem in GetCandidateLootItemsFromInventory(nestedContainer.Inventory))
                {
                    yield return nestedItem;
                }
            }
        }

        private bool IsValidWreckLoot(Item item)
        {
            if (item == null || item.Removed || item.NonInteractable || ignoredItems.Contains(item))
            {
                return false;
            }

            if (item.HasTag(Tags.OxygenSource))
            {
                return false;
            }

            if (Vector2.DistanceSquared(character.WorldPosition, item.WorldPosition) > SearchRadius * SearchRadius)
            {
                return false;
            }

            if (IsPortableContainerLoot(item) && RetrieveItemsOrderRules.IsMarkedContainer(item))
            {
                return item.Submarine != homeSubmarine;
            }

            if (!TryGetRootContainerItem(item, out Item rootContainer) ||
                !RetrieveItemsOrderRules.IsMarkedContainer(rootContainer) ||
                rootContainer.Submarine == homeSubmarine)
            {
                return false;
            }

            return true;
        }

        private bool IsPortableContainerLoot(Item item)
        {
            return RetrieveItemsOrderRules.IsPortableRetrievalTarget(item) ||
                (item != null &&
                 item.GetComponent<ItemContainer>() != null &&
                 portableContainerLootTags.Any(item.HasTag));
        }

        private bool TryGetRootContainerItem(Item item, out Item containerItem)
        {
            containerItem = null;
            Item currentItem = item;
            Item lastContainerItem = null;

            while (currentItem?.ParentInventory != null)
            {
                object parentInventory = currentItem.ParentInventory;
                object owner =
                    AccessTools.Property(parentInventory.GetType(), "Owner")?.GetValue(parentInventory) ??
                    AccessTools.Field(parentInventory.GetType(), "Owner")?.GetValue(parentInventory) ??
                    AccessTools.Field(parentInventory.GetType(), "owner")?.GetValue(parentInventory);

                if (owner is not Item ownerItem)
                {
                    break;
                }

                lastContainerItem = ownerItem;
                currentItem = ownerItem;
            }

            containerItem = lastContainerItem;
            return containerItem != null;
        }

        private Item GetEquippedDivingGear()
        {
            return character.Inventory?.AllItems.FirstOrDefault(item =>
                IsPressureProtectiveDivingGear(item) &&
                item.Equipper == character);
        }

        private Item FindAvailableDivingGear()
        {
            return Item.ItemList
                .Where(item =>
                    item != null &&
                    !item.Removed &&
                    item.Submarine == homeSubmarine &&
                    IsPressureProtectiveDivingGear(item))
                .OrderByDescending(item => item.Equipper == character)
                .ThenByDescending(item => item.HasTag(deepDivingTag))
                .ThenBy(item => Vector2.DistanceSquared(character.WorldPosition, item.WorldPosition))
                .FirstOrDefault();
        }

        private bool IsPressureProtectiveDivingGear(Item item)
        {
            if (item == null ||
                item.Removed ||
                !item.HasTag(divingTag))
            {
                return false;
            }

            Wearable wearable = item.GetComponent<Wearable>();
            return wearable != null && GetPressureProtection(wearable) > 0.0f;
        }

        private float GetPressureProtection(Wearable wearable)
        {
            object pressureProtection = AccessTools.Field(typeof(Wearable), "PressureProtection")?.GetValue(wearable);
            if (pressureProtection is float floatValue)
            {
                return floatValue;
            }

            if (pressureProtection is double doubleValue)
            {
                return (float)doubleValue;
            }

            if (pressureProtection is int intValue)
            {
                return intValue;
            }

            return 0.0f;
        }

        private float GetActiveOxygenRatio()
        {
            return GetActiveOxygenRatio(GetEquippedDivingGear());
        }

        private float GetActiveOxygenRatio(Item divingGear)
        {
            Item tank = GetContainedOxygenSource(divingGear);
            return tank == null ? 0.0f : GetConditionRatio(tank);
        }

        private Item GetContainedOxygenSource(Item containerItem)
        {
            ItemContainer itemContainer = containerItem?.GetComponent<ItemContainer>();
            if (itemContainer?.Inventory == null)
            {
                return null;
            }

            return GetDirectInventoryItems(itemContainer.Inventory)
                .Where(item => item != null && !item.Removed && item.HasTag(Tags.OxygenSource))
                .OrderByDescending(GetConditionRatio)
                .FirstOrDefault();
        }

        private Item FindFullOxygenTank()
        {
            return Item.ItemList
                .Where(item =>
                    item != null &&
                    !item.Removed &&
                    item.Submarine == homeSubmarine &&
                    (item.HasTag(oxygenTankContainerTag) ||
                     item.HasTag(oxygenTankRefillerTag) ||
                     IsOxygenGeneratorStorage(item)))
                .SelectMany(container => GetDirectInventoryItems(container.GetComponent<ItemContainer>()?.Inventory))
                .Where(item =>
                    item != null &&
                    !item.Removed &&
                    item.HasTag(Tags.OxygenSource) &&
                    GetConditionRatio(item) >= PreTripOxygenRatio)
                .OrderByDescending(GetConditionRatio)
                .FirstOrDefault();
        }

        private static bool IsOxygenGeneratorStorage(Item item)
        {
            string identifier = item?.Prefab?.Identifier.Value ?? string.Empty;
            return identifier.IndexOf("oxygengenerator", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   item.GetComponent<ItemContainer>()?.Inventory != null;
        }

        private void UnequipDivingGearIfIdle()
        {
            if (!IsSafeToUnequipDivingGear())
            {
                return;
            }

            Item divingGear = GetEquippedDivingGear();
            if (divingGear == null)
            {
                return;
            }

            divingGear.Unequip(character);
            IEnumerable<InvSlotType> anySlots =
                AccessTools.Field(typeof(CharacterInventory), "AnySlot")?.GetValue(null) as IEnumerable<InvSlotType> ??
                Enumerable.Empty<InvSlotType>();
            character.Inventory?.TryPutItem(divingGear, character, anySlots, false, true, false);
        }

        private bool IsSafeToUnequipDivingGear()
        {
            Hull hull = character.CurrentHull;
            if (hull == null || hull.Submarine != homeSubmarine)
            {
                return false;
            }

            string hullName = GetHullName(hull);
            if (hull.IsAirlock ||
                hullName.IndexOf("airlock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                hullName.IndexOf("docking", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return GetHullWaterRatio(hull) <= 0.05f;
        }

        private static string GetHullName(Hull hull)
        {
            if (hull == null)
            {
                return "<null>";
            }

            object name =
                AccessTools.Property(hull.GetType(), "RoomName")?.GetValue(hull) ??
                AccessTools.Property(hull.GetType(), "DisplayName")?.GetValue(hull) ??
                AccessTools.Property(hull.GetType(), "Name")?.GetValue(hull) ??
                AccessTools.Field(hull.GetType(), "roomName")?.GetValue(hull) ??
                AccessTools.Field(hull.GetType(), "name")?.GetValue(hull);

            return name?.ToString() ?? string.Empty;
        }

        private static float GetHullWaterRatio(Hull hull)
        {
            object waterVolumeObject =
                AccessTools.Property(hull.GetType(), "WaterVolume")?.GetValue(hull) ??
                AccessTools.Field(hull.GetType(), "waterVolume")?.GetValue(hull);
            object volumeObject =
                AccessTools.Property(hull.GetType(), "Volume")?.GetValue(hull) ??
                AccessTools.Field(hull.GetType(), "volume")?.GetValue(hull);

            if (waterVolumeObject == null || volumeObject == null)
            {
                return 1.0f;
            }

            float waterVolume = Convert.ToSingle(waterVolumeObject);
            float volume = Math.Max(Convert.ToSingle(volumeObject), 1.0f);
            return MathHelper.Clamp(waterVolume / volume, 0.0f, 1.0f);
        }

        private bool TryInstallOxygenTank(Item divingGear, Item newTank)
        {
            if (divingGear == null || newTank == null || newTank.Removed || !newTank.HasTag(Tags.OxygenSource))
            {
                return false;
            }

            ItemContainer gearContainer = divingGear.GetComponent<ItemContainer>();
            if (gearContainer?.Inventory == null)
            {
                return false;
            }

            Item oldTank = GetContainedOxygenSource(divingGear);
            if (oldTank == newTank)
            {
                return true;
            }

            IEnumerable<InvSlotType> anySlots =
                AccessTools.Field(typeof(CharacterInventory), "AnySlot")?.GetValue(null) as IEnumerable<InvSlotType> ??
                Enumerable.Empty<InvSlotType>();

            if (oldTank != null &&
                character.Inventory?.TryPutItem(oldTank, character, anySlots, false, true, false) != true)
            {
                return false;
            }

            return gearContainer.Inventory.TryPutItem(newTank, character, Enumerable.Empty<InvSlotType>(), false, true, false);
        }

        private bool IsItemInCharacterInventory(Item item)
        {
            return item != null &&
                character.Inventory != null &&
                character.Inventory.AllItems.Contains(item);
        }

        private float GetConditionRatio(Item item)
        {
            if (item == null)
            {
                return 0.0f;
            }

            float maxCondition = Math.Max(item.MaxCondition, 1.0f);
            return MathHelper.Clamp(item.Condition / maxCondition, 0.0f, 1.0f);
        }

        private int CountCarriedLoot()
        {
            return GetCarriedLoot().Count();
        }

        private IEnumerable<Item> GetCarriedLoot()
        {
            if (character.Inventory == null)
            {
                yield break;
            }

            foreach (Item item in GetDirectInventoryItems(character.Inventory))
            {
                if (IsRetrievedLootItem(item))
                {
                    yield return item;
                }
            }
        }

        private bool IsRetrievedLootItem(Item item)
        {
            return item != null &&
                !item.Removed &&
                !initialInventoryItems.Contains(item) &&
                !item.HasTag(Tags.OxygenSource) &&
                item != GetEquippedDivingGear();
        }

        private bool DropNextLootToSubFloor()
        {
            Item itemToDrop = GetCarriedLoot().FirstOrDefault();
            if (itemToDrop == null || character.Inventory == null)
            {
                return false;
            }

            Hull dropHull = character.CurrentHull;
            if (dropHull == null || dropHull.Submarine != homeSubmarine)
            {
                dropHull = Hull.HullList.FirstOrDefault(h => h.Submarine == homeSubmarine);
            }

            if (dropHull == null)
            {
                return false;
            }

            itemToDrop.Drop(character, createNetworkEvent: true, setTransform: true);
            itemToDrop.SetTransform(dropHull.WorldPosition, 0.0f);
            return true;
        }

        private bool IsOnHomeSubmarine()
        {
            return character.Submarine == homeSubmarine ||
                   character.CurrentHull?.Submarine == homeSubmarine;
        }

        private bool HasReturnedHome()
        {
            return IsOnHomeSubmarine();
        }

        private bool IsCloseToCurrentTarget(float distance)
        {
            return currentTargetItem != null &&
                   !currentTargetItem.Removed &&
                   Vector2.DistanceSquared(character.WorldPosition, currentTargetItem.WorldPosition) <= distance * distance;
        }

        private void BeginReturning()
        {
            ReleaseExitAirlockDoorCommand();
            StopOpenWaterFallback();
            ClearSubObjective();
            state = WreckRetrieveState.Returning;
            statusTimer = 0.0f;
            ResetStuckTracking();
        }

        private bool ShouldAbortForInjury()
        {
            if (character.IsUnconscious)
            {
                return true;
            }

            float maxVitality = Math.Max(character.MaxVitality, 1.0f);
            return character.Vitality / maxVitality <= 0.25f;
        }

        private bool IsSubObjectiveActive()
        {
            return currentSubObjective != null && !currentSubObjective.IsCompleted && !currentSubObjective.Abandon;
        }

        private bool IsSubObjectiveFinished()
        {
            return currentSubObjective != null && (currentSubObjective.IsCompleted || currentSubObjective.Abandon);
        }

        private bool IsStuckOnCurrentSubObjective()
        {
            return currentSubObjective != null && stuckTimer >= StuckTimeout;
        }

        private void UpdateStuckTimer(float deltaTime)
        {
            if (Vector2.DistanceSquared(character.WorldPosition, lastWorldPosition) > StuckDistanceThreshold * StuckDistanceThreshold)
            {
                lastWorldPosition = character.WorldPosition;
                stuckTimer = 0.0f;
                return;
            }

            if (currentSubObjective != null || usingOpenWaterFallback)
            {
                stuckTimer += deltaTime;
            }
        }

        private void ResetStuckTracking()
        {
            lastWorldPosition = character.WorldPosition;
            stuckTimer = 0.0f;
        }

        private void ClearSubObjective()
        {
            if (currentSubObjective == null)
            {
                return;
            }

            RemoveSubObjective(ref currentSubObjective);
        }

        private void ClearTarget()
        {
            ReleaseExitAirlockDoorCommand();
            StopOpenWaterFallback();
            currentTargetItem = null;
            pendingOxygenTank = null;
        }

        private void CaptureInitialInventoryItems()
        {
            initialInventoryItems.Clear();
            if (character.Inventory == null)
            {
                return;
            }

            foreach (Item item in character.Inventory.AllItems)
            {
                if (item != null && !item.Removed)
                {
                    initialInventoryItems.Add(item);
                }
            }
        }

        private static IEnumerable<Item> GetDirectInventoryItems(object inventory)
        {
            if (inventory == null)
            {
                yield break;
            }

            object directItems =
                AccessTools.Property(inventory.GetType(), "Items")?.GetValue(inventory) ??
                AccessTools.Field(inventory.GetType(), "Items")?.GetValue(inventory) ??
                AccessTools.Field(inventory.GetType(), "items")?.GetValue(inventory);

            List<Item> items = new List<Item>();
            if (directItems is IEnumerable enumerable)
            {
                HashSet<Item> yielded = new HashSet<Item>();
                foreach (object value in enumerable)
                {
                    if (value is Item item && yielded.Add(item))
                    {
                        items.Add(item);
                    }
                }
            }

            if (items.Count == 0)
            {
                object allItems =
                    AccessTools.Property(inventory.GetType(), "AllItems")?.GetValue(inventory) ??
                    AccessTools.Field(inventory.GetType(), "AllItems")?.GetValue(inventory) ??
                    AccessTools.Field(inventory.GetType(), "allItems")?.GetValue(inventory);

                if (allItems is IEnumerable allItemsEnumerable)
                {
                    HashSet<Item> yielded = new HashSet<Item>();
                    foreach (object value in allItemsEnumerable)
                    {
                        if (value is Item item && item.ParentInventory == inventory && yielded.Add(item))
                        {
                            items.Add(item);
                        }
                    }
                }
            }

            foreach (Item item in items)
            {
                yield return item;
            }
        }

        private void Speak(string message, Identifier identifier, float minDurationBetweenSimilar, bool force = false)
        {
            if ((!force && statusTimer > 0.0f) || !character.IsOnPlayerTeam)
            {
                return;
            }

            character.Speak(RetrieveItemsOrderRules.GetText(identifier, message), identifier: identifier, minDurationBetweenSimilar: minDurationBetweenSimilar);
            statusTimer = minDurationBetweenSimilar;
        }
    }
}
