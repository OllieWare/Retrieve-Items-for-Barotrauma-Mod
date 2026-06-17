using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace RetrieveItemsOrderMod
{
    internal sealed class AIObjectiveRetrieveItems : AIObjective
    {
        private enum RetrieveState
        {
            Searching,
            Returning,
            Depositing,
            Finished
        }

        // Tuning values modders are likely to want to adjust first.
        private const float AbandonVitalityRatio = 0.25f;
        private const float SearchRadius = 12000f;
        private const float StatusCooldown = 5.0f;
        private const int MinimumFreeSlotsBeforeReturn = 1;
        private const float StuckDistanceThreshold = 50.0f;
        private const float StuckTimeout = 5.0f;
        private const float LogicLogInterval = 1.0f;

        private readonly Order sourceOrder;
        private Submarine homeSubmarine;
        private readonly HashSet<Item> ignoredItems = new HashSet<Item>();
        private readonly Dictionary<Item, int> observedMarkVersions = new Dictionary<Item, int>();
        private readonly HashSet<Item> initialInventoryItems = new HashSet<Item>();
        private readonly HashSet<Item> initialEquippedWearables = new HashSet<Item>();
        private readonly Identifier[] ignoredTags =
        {
            Tags.OxygenSource
        };
        private readonly Identifier[] portableContainerLootTags =
        {
            "crate".ToIdentifier(),
            "ammobox".ToIdentifier(),
            "mobilecontainer".ToIdentifier(),
            "artifactcontainer".ToIdentifier()
        };
        private readonly Identifier mobileContainerTag = "mobilecontainer".ToIdentifier();
        private readonly Identifier smallItemTag = "smallitem".ToIdentifier();

        private RetrieveState state = RetrieveState.Searching;
        private AIObjective currentSubObjective;
        private Submarine targetOutpost;
        private Item currentTargetItem;
        private Item currentTargetContainer;
        private Item centeredTargetContainer;
        private Hull centeredTargetHull;
        private Item centeringTargetContainer;
        private Hull centeringTargetHull;
        private Item lastAttemptedItem;
        private Hull currentTargetHull;
        private float statusTimer;
        private float logicLogTimer;
        private float stuckTimer;
        private string currentLogicStep;
        private bool returningAfterInjury;
        private int lastCarriedCount;
        private int sameTargetAttempts;
        private Vector2 lastWorldPosition;

        public override Identifier Identifier { get; set; } = RetrieveItemsIds.OrderIdentifier;
        public override string DebugTag => $"{Identifier} ({state})";
        public override bool AllowOutsideSubmarine => true;
        public override bool AllowInFriendlySubs => true;
        public override bool AllowInAnySub => true;
        public override bool KeepDivingGearOn => state != RetrieveState.Depositing && state != RetrieveState.Finished;

        public AIObjectiveRetrieveItems(Character character, AIObjectiveManager objectiveManager, Order order, float priorityModifier = 1.0f)
            : base(character, objectiveManager, priorityModifier, RetrieveItemsIds.OrderIdentifier)
        {
            sourceOrder = order;
            homeSubmarine = null;
            targetOutpost = null;
            currentTargetContainer = null;
            currentTargetHull = null;
            lastCarriedCount = 0;
            lastWorldPosition = character.WorldPosition;
            logicLogTimer = 0.0f;
            stuckTimer = 0.0f;
            currentLogicStep = null;
            returningAfterInjury = false;
            lastAttemptedItem = null;
            sameTargetAttempts = 0;
            CaptureInitialInventoryItems();
            // LuaCsLogger.Log($"[RetrieveItemsOrder] Objective ctor for {character.Name}, order option={order.Option}, objective option={Option}");
        }

        public override bool CheckObjectiveState()
        {
            return IsCompleted;
        }

        public void SpeakOrderReceived()
        {
            if (!character.IsOnPlayerTeam)
            {
                return;
            }

            // LuaCsLogger.Log($"[RetrieveItemsOrder] Acknowledging order for {character.Name}");
            character.Speak(
                RetrieveItemsOrderRules.GetText(RetrieveItemsIds.OrderReceivedDialog, "Got it, starting retrieval."),
                identifier: RetrieveItemsIds.OrderReceivedDialog,
                minDurationBetweenSimilar: 1.0f);
        }

        public override float GetPriority()
        {
            bool isOrder = objectiveManager.IsOrder(this);
            if (character.IsDead)
            {
                Priority = 0.0f;
                Abandon = !isOrder;
                return Priority;
            }

            if (state == RetrieveState.Depositing && CountCarriedLoot() > 0)
            {
                Priority = Math.Max(objectiveManager.GetOrderPriority(this), objectiveManager.GetCurrentPriority() + 10.0f);
                return Priority;
            }

            Priority = isOrder ? objectiveManager.GetOrderPriority(this) : 10.0f;
            return Priority;
        }

        public override void Act(float deltaTime)
        {
            statusTimer -= deltaTime;
            logicLogTimer -= deltaTime;
            SetLogicStep($"{state} - Act");
            UpdateStuckTimer(deltaTime);

            if (homeSubmarine == null)
            {
                homeSubmarine = RetrieveItemsOrderRules.ResolveHomeSubmarine(character);
            }

            if (lastCarriedCount == 0)
            {
                lastCarriedCount = CountCarriedLoot();
            }

            if (!returningAfterInjury && ShouldAbortForInjury())
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Returning after severe injury: {character.Name}");
                statusTimer = 0.0f;
                Speak("Too injured to continue. Returning to the submarine.", RetrieveItemsIds.SevereInjuryDialog, 2.0f);
                ClearSubObjective();
                returningAfterInjury = true;
                BeginReturning();
                if (character.Submarine == homeSubmarine || character.CurrentHull?.Submarine == homeSubmarine)
                {
                    Abandon = true;
                    return;
                }
            }

            if (!returningAfterInjury)
            {
                targetOutpost ??= RetrieveItemsOrderRules.FindTargetLocation(character);
                if (targetOutpost == null)
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] No target location found for {character.Name}. HomeSub={homeSubmarine?.Info?.Name}, CharacterSub={character.Submarine?.Info?.Name}");
                    Speak("I can't find an abandoned outpost to loot.", RetrieveItemsIds.NoTargetDialog, 2.0f);
                    Abandon = true;
                    return;
                }

                if (RetrieveItemsOrderRules.HasHostiles(targetOutpost, character))
                {
                    Speak("Hostiles are still active in the outpost. Cancelling retrieval.", RetrieveItemsIds.HostilesDialog, 2.0f);
                    Abandon = true;
                    return;
                }
            }

            switch (state)
            {
                case RetrieveState.Searching:
                    UpdateSearching();
                    break;
                case RetrieveState.Returning:
                    UpdateReturning();
                    break;
                case RetrieveState.Depositing:
                    UpdateDepositing(deltaTime);
                    break;
                case RetrieveState.Finished:
                    UpdateFinished();
                    break;
            }

            FlushLogicStepLog();
        }

        /// <summary>
        /// Item scanning only targets loose, movable floor items in the chosen outpost.
        /// Adjust SearchRadius or IsValidLoot to make the bot more or less selective.
        /// </summary>
        private void UpdateSearching()
        {
            SetLogicStep("Searching - Act");
            Speak("Searching...", RetrieveItemsIds.SearchDialog, StatusCooldown);

            if (currentSubObjective?.Abandon == true && currentTargetItem != null)
            {
                if (CountCarriedLoot() > 0)
                {
                    SetLogicStep("Searching - Pickup abandoned with carried loot, returning");
                    ClearSubObjective();
                    ClearCurrentSearchTarget();
                    BeginReturning();
                    return;
                }

                SetLogicStep("Searching - Marking abandoned target ignored");
                ignoredItems.Add(currentTargetItem);
                ClearCurrentSearchTarget();
            }
            if (IsSubObjectiveFinished())
            {
                bool finishedPickup = currentSubObjective is AIObjectiveGetItem && currentSubObjective.IsCompleted;
                bool finishedPortableCargoPickup = finishedPickup && IsPortableCargo(currentTargetItem);
                bool finishedCentering = !finishedPickup &&
                    currentSubObjective?.IsCompleted == true &&
                    centeringTargetContainer != null &&
                    centeringTargetHull != null &&
                    character.CurrentHull == centeringTargetHull;
                if (finishedPickup)
                {
                    MoveRetrievedWearableOutOfEquipSlot();
                    UnmarkCurrentTargetContainerIfFullyRetrieved();
                }
                else if (finishedCentering)
                {
                    centeredTargetContainer = centeringTargetContainer;
                    centeredTargetHull = centeringTargetHull;
                }
                ClearCenteringTarget();
                SetLogicStep("Searching - Clearing finished subobjective");
                ClearSubObjective();
                if (finishedPortableCargoPickup && CountCarriedLoot() > 0)
                {
                    SetLogicStep("Searching - Portable cargo picked up, returning");
                    ClearCurrentSearchTarget();
                    BeginReturning();
                    return;
                }
            }

            if (IsStuckOnCurrentSubObjective())
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Search subobjective stuck for {character.Name}, skipping current target");
                if (currentTargetItem != null)
                {
                    if (CountCarriedLoot() > 0)
                    {
                        SetLogicStep("Searching - Stuck with carried loot, returning");
                        ClearCurrentSearchTarget();
                        BeginReturning();
                        return;
                    }

                    ignoredItems.Add(currentTargetItem);
                }
                ClearCurrentSearchTarget();
                ClearSubObjective();
                ClearCenteringTarget();
                ResetStuckTracking();
            }

            if (ShouldReturnWithCurrentLoot())
            {
                SetLogicStep("Searching - Switching to return");
                ClearSubObjective();
                BeginReturning();
                return;
            }

            if (IsSubObjectiveActive())
            {
                SetLogicStep("Searching - Waiting on subobjective");
                return;
            }

            if (currentTargetItem != null)
            {
                if (!IsValidLoot(currentTargetItem) || !TryResolveTargetContainer(currentTargetItem, out currentTargetContainer))
                {
                    SetLogicStep("Searching - Ignoring invalid target");
                    ignoredItems.Add(currentTargetItem);
                    ClearCurrentSearchTarget();
                }
                else
                {
                    SetLogicStep("Searching - Retrying current target");
                    currentTargetHull = GetTargetHull(currentTargetItem, currentTargetContainer);
                    if (TryCreateSearchSubObjectiveForCurrentTarget())
                    {
                        return;
                    }
                }
            }

            SetLogicStep("Searching - Finding next loose item");
            currentTargetItem = FindNextLooseItem();
            if (currentTargetItem == null)
            {
                if (CountCarriedLoot() > 0)
                {
                    SetLogicStep("Searching - No more loot, switching to return");
                    BeginReturning();
                }
                return;
            }

            if (currentTargetItem == lastAttemptedItem)
            {
                sameTargetAttempts++;
                if (sameTargetAttempts >= 3)
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] Skipping repeatedly failed item for {character.Name}: {currentTargetItem.Name}");
                    if (CountCarriedLoot() > 0)
                    {
                        SetLogicStep("Searching - Repeated target failure with carried loot, returning");
                        ClearCurrentSearchTarget();
                        BeginReturning();
                        return;
                    }

                    ignoredItems.Add(currentTargetItem);
                    ClearCurrentSearchTarget();
                    return;
                }
            }
            else
            {
                lastAttemptedItem = currentTargetItem;
                sameTargetAttempts = 1;
            }

            if (!TryResolveTargetContainer(currentTargetItem, out currentTargetContainer))
            {
                SetLogicStep("Searching - Could not resolve target container");
                ignoredItems.Add(currentTargetItem);
                ClearCurrentSearchTarget();
                return;
            }

            SetLogicStep($"Searching - Targeting {currentTargetItem.Name}");

            currentTargetHull = GetTargetHull(currentTargetItem, currentTargetContainer);
            lastCarriedCount = CountCarriedLoot();
            if (ShouldReturnBeforePickup(currentTargetItem))
            {
                SetLogicStep("Searching - Carry limit reached before next pickup");
                ClearCurrentSearchTarget();
                BeginReturning();
                return;
            }

            TryCreateSearchSubObjectiveForCurrentTarget();
        }

        private bool TryCreateSearchSubObjectiveForCurrentTarget()
        {
            if (currentTargetItem == null || currentTargetContainer == null || currentTargetHull == null)
            {
                return false;
            }

            if (character.CurrentHull != currentTargetHull)
            {
                SetLogicStep($"Searching - Moving to {GetHullName(currentTargetHull)}");
                return CreateCenteringSubObjective();
            }

            if (centeredTargetContainer != currentTargetContainer || centeredTargetHull != currentTargetHull)
            {
                SetLogicStep($"Searching - Centering in {GetHullName(currentTargetHull)}");
                return CreateCenteringSubObjective();
            }

            SetLogicStep($"Searching - Retrieving {currentTargetItem.Name}");
            currentSubObjective = new AIObjectiveGetItem(character, currentTargetItem, objectiveManager, equip: false)
            {
                MustBeSpecificItem = true,
                Wear = false,
                AllowStealing = true
            };

            AddSubObjective(currentSubObjective);
            return true;
        }

        private bool CreateCenteringSubObjective()
        {
            currentSubObjective = CreateGoToHullCenterObjective(currentTargetHull) ??
                new AIObjectiveGoTo(currentTargetContainer, character, objectiveManager, repeat: false, getDivingGearIfNeeded: true, priorityModifier: 1.0f, closeEnough: 100.0f)
                {
                    AllowGoingOutside = false
                };
            centeringTargetContainer = currentTargetContainer;
            centeringTargetHull = currentTargetHull;
            AddSubObjective(currentSubObjective);
            return true;
        }

        private AIObjective CreateGoToHullCenterObjective(Hull hull)
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
                    true,
                    1.0f,
                    100.0f
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
                    return objective;
                }
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to create hull-center objective: {ex.Message}");
            }

            return null;
        }

        private void UpdateReturning()
        {
            SetLogicStep("Returning - Act");
            Speak("Returning with items...", RetrieveItemsIds.ReturnDialog, StatusCooldown);

            if (HasVanillaReturnCompleted())
            {
                SetLogicStep("Returning - Vanilla return completed");
                ClearSubObjective();
                FinishReturning();
                return;
            }

            if (currentSubObjective?.Abandon == true)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Return subobjective abandoned for {character.Name}");
                Speak("I can't get back to the submarine.", AIObjectiveGoTo.DialogCannotReachPlace, 2.0f);
                Abandon = true;
                return;
            }

            if (IsSubObjectiveFinished())
            {
                if (HasVanillaReturnCompleted())
                {
                    SetLogicStep("Returning - Return objective completed");
                    ClearSubObjective();
                    FinishReturning();
                    return;
                }

                SetLogicStep("Returning - Clearing finished subobjective");
                ClearSubObjective();
            }

            if (IsStuckOnCurrentSubObjective())
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Return subobjective stuck for {character.Name}");
                ClearSubObjective();
                Abandon = true;
                return;
            }

            if (!IsSubObjectiveActive())
            {
                SetLogicStep("Returning - Creating return objective");
                currentSubObjective = new AIObjectiveReturn(character, sourceOrder.OrderGiver, objectiveManager);
                SyncHomeSubmarineFromReturnObjective(currentSubObjective);
                currentSubObjective.Completed += OnReturnSubObjectiveCompleted;
                AddSubObjective(currentSubObjective);
                // LuaCsLogger.Log($"[RetrieveItemsOrder] Vanilla return target for {character.Name}: target={GetReturnTargetName(currentSubObjective)}, charSub={character.Submarine?.Info?.Name ?? "<null>"}, hull={GetHullName(character.CurrentHull)}");
                return;
            }

            SetLogicStep($"Returning - Waiting on vanilla return ({GetReturnStatus(currentSubObjective)})");
        }

        private bool HasVanillaReturnCompleted()
        {
            if (currentSubObjective is not AIObjectiveReturn returnObjective)
            {
                return false;
            }

            SyncHomeSubmarineFromReturnObjective(returnObjective);
            returnObjective.CheckObjectiveState();
            return returnObjective.IsCompleted ||
                   (returnObjective.Target != null &&
                    (character.Submarine == returnObjective.Target ||
                     character.CurrentHull?.Submarine == returnObjective.Target));
        }

        private void SyncHomeSubmarineFromReturnObjective(AIObjective objective)
        {
            if (objective is AIObjectiveReturn returnObjective && returnObjective.Target != null)
            {
                homeSubmarine = returnObjective.Target;
            }
        }

        private string GetReturnStatus(AIObjective objective)
        {
            return $"target={GetReturnTargetName(objective)}, charSub={character.Submarine?.Info?.Name ?? "<null>"}, hull={GetHullName(character.CurrentHull)}, currentHullSub={character.CurrentHull?.Submarine?.Info?.Name ?? "<null>"}, hullIsAirlock={character.CurrentHull?.IsAirlock.ToString() ?? "<null>"}, completed={objective?.IsCompleted.ToString() ?? "<null>"}, abandon={objective?.Abandon.ToString() ?? "<null>"}";
        }

        private static string GetReturnTargetName(AIObjective objective)
        {
            return objective is AIObjectiveReturn returnObjective
                ? returnObjective.Target?.Info?.Name ?? "<null>"
                : "<not-return>";
        }

        private static string GetHullName(Hull hull)
        {
            return hull?.DisplayName.ToString() ?? "<null>";
        }

        private void OnReturnSubObjectiveCompleted()
        {
            if (state != RetrieveState.Returning)
            {
                return;
            }

            // LuaCsLogger.Log($"[RetrieveItemsOrder] Vanilla return completion callback for {character.Name}: {GetReturnStatus(currentSubObjective)}");
            ClearSubObjective();
            FinishReturning();
            ResetStuckTracking();
            if (!Abandon && state == RetrieveState.Depositing)
            {
                statusTimer = 0.0f;
                UpdateDepositing(0.1f);
            }
        }

        private void BeginReturning()
        {
            state = RetrieveState.Returning;
            statusTimer = 0.0f;
            ClearCenteredSearchTarget();
            ResetStuckTracking();
        }

        private void FinishReturning()
        {
            if (returningAfterInjury)
            {
                Abandon = true;
                return;
            }

            state = RetrieveState.Depositing;
            statusTimer = 0.0f;
        }

        private void UpdateDepositing(float deltaTime)
        {
            SetLogicStep("Depositing - Act");
            if (CountCarriedLoot() <= 0)
            {
                SetLogicStep("Depositing - Finished");
                statusTimer = 0.0f;
                Speak("Loot secured.", RetrieveItemsIds.DoneDialog, 2.0f);
                state = RetrieveState.Finished;
                return;
            }

            Speak("Depositing retrieved items.", RetrieveItemsIds.DepositDialog, StatusCooldown);

            if (IsSubObjectiveFinished())
            {
                if (currentSubObjective.Abandon && CountCarriedLoot() >= lastCarriedCount)
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] Deposit subobjective abandoned for {character.Name}, dropping one item to floor");
                    if (!DropNextLootToSubFloor())
                    {
                        Speak("I can't find anywhere appropriate to store the remaining loot.", RetrieveItemsIds.CannotStoreDialog, 2.0f);
                        Abandon = true;
                        return;
                    }
                }
                SetLogicStep("Depositing - Clearing finished subobjective");
                ClearSubObjective();
            }

            if (IsStuckOnCurrentSubObjective())
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Deposit subobjective stuck for {character.Name}, dropping one item to floor");
                ClearSubObjective();
                if (!DropNextLootToSubFloor())
                {
                    Abandon = true;
                    return;
                }
                ResetStuckTracking();
            }

            if (!IsSubObjectiveActive())
            {
                List<Item> carriedLoot = GetCarriedLoot().ToList();
                if (carriedLoot.Count == 0)
                {
                    SetLogicStep("Depositing - No carried loot");
                    state = RetrieveState.Finished;
                    return;
                }

                // Reuse the same objective class vanilla uses for the Cleanup Items command.
                // This makes container selection follow vanilla "put items where they belong"
                // logic instead of a custom tagged target container.
                SetLogicStep("Depositing - Creating cleanup objective");
                lastCarriedCount = carriedLoot.Count;
                Item itemToDeposit = carriedLoot.First();
                currentSubObjective = new AIObjectiveCleanupItem(itemToDeposit, character, objectiveManager, 1.0f);
                currentSubObjective.Abandoned += () =>
                    LuaCsLogger.Log($"[RetrieveItemsOrder] Cleanup objective abandoned for {character.Name}, carried={CountCarriedLoot()}");
                AddSubObjective(currentSubObjective);
                // LuaCsLogger.Log($"[RetrieveItemsOrder] Created single-item cleanup objective for {character.Name}, carried={carriedLoot.Count}, item={itemToDeposit.Name}");
                currentSubObjective.Act(deltaTime);
                return;
            }

            SetLogicStep($"Depositing - Waiting on {currentSubObjective.GetType().Name}");
        }

        private void UpdateFinished()
        {
            SetLogicStep("Finished - Waiting");

            if (ShouldCompleteBecauseRetrievalContextEnded())
            {
                SetLogicStep("Finished - Retrieval context ended");
                IsCompleted = true;
                return;
            }

            if (currentTargetItem == null)
            {
                currentTargetItem = FindNextLooseItem();
            }

            if (currentTargetItem != null)
            {
                SetLogicStep("Finished - Starting next retrieval loop");
                state = RetrieveState.Searching;
            }
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

            itemToDrop.Drop(character);
            itemToDrop.SetTransform(dropHull.WorldPosition, 0.0f);
            itemToDrop.Submarine = homeSubmarine;
            lastCarriedCount = CountCarriedLoot();
            return true;
        }

        private Item FindNextLooseItem()
        {
            Item nextItem = GetCandidateLootItemsFromMarkedContainers()
                .OrderByDescending(item => character.CurrentHull != null && GetContainerHull(item) == character.CurrentHull)
                .ThenBy(item => character.CurrentHull == null ? 0 : 1)
                .ThenBy(item => Vector2.DistanceSquared(character.WorldPosition, GetContainerPosition(item)))
                .FirstOrDefault();
            if (nextItem == null)
            {
                int markedCount = RetrieveItemsOrderRules.GetMarkedContainers(targetOutpost).Count();
                SetLogicStep($"Searching - No valid marked loot (marked={markedCount})");
            }
            return nextItem;
        }

        private IEnumerable<Item> GetCandidateLootItemsFromMarkedContainers()
        {
            IEnumerable<Item> marked = RetrieveItemsOrderRules.GetMarkedContainers(targetOutpost);
            foreach (Item container in marked)
            {
                if (container == null || container.Removed)
                {
                    continue;
                }

                if (IsMarkedPortableLoot(container))
                {
                    if (IsValidLoot(container))
                    {
                        yield return container;
                    }
                    continue;
                }

                ItemContainer itemContainer = container.GetComponent<ItemContainer>();
                if (itemContainer?.Inventory == null)
                {
                    continue;
                }

                foreach (Item item in GetCandidateLootItemsFromInventory(itemContainer.Inventory))
                {
                    if (IsValidLoot(item))
                    {
                        yield return item;
                    }
                }
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

        private bool IsPortableContainerLoot(Item item)
        {
            return item != null &&
                item.GetComponent<ItemContainer>() != null &&
                portableContainerLootTags.Any(item.HasTag);
        }

        private bool IsMarkedPortableLoot(Item item)
        {
            return item != null &&
                RetrieveItemsOrderRules.IsMarkedContainer(item) &&
                IsPortableContainerLoot(item);
        }

        private void UnmarkCurrentTargetContainerIfFullyRetrieved()
        {
            if (currentTargetContainer == null || !RetrieveItemsOrderRules.IsMarkedContainer(currentTargetContainer))
            {
                return;
            }

            ItemContainer itemContainer = currentTargetContainer.GetComponent<ItemContainer>();
            if (itemContainer?.Inventory == null)
            {
                return;
            }

            bool hasRemainingLoot = GetCandidateLootItemsFromInventory(itemContainer.Inventory).Any(IsValidLoot);
            if (hasRemainingLoot)
            {
                return;
            }

            RetrieveItemsOrderRules.SetMarkedContainerState(currentTargetContainer, false);
            RetrieveItemsOrderRules.BroadcastMarkContainerRelay(currentTargetContainer, false);
            // LuaCsLogger.Log($"[RetrieveItemsOrder] Auto-unmarked emptied container: {currentTargetContainer.Name}");
        }

        private bool IsValidLoot(Item item)
        {
            if (item == null || item.Removed || item.NonInteractable)
            {
                return false;
            }

            RefreshIgnoredStateForItem(item);
            if (ignoredItems.Contains(item))
            {
                return false;
            }

            if (IsMarkedPortableLoot(item))
            {
                return IsValidMarkedPortableLoot(item);
            }

            if (item.ParentInventory == null)
            {
                return false;
            }

            if (!TryGetRootContainerItem(item, out Item containerItem))
            {
                return false;
            }

            if (containerItem.Submarine != targetOutpost)
            {
                return false;
            }

            if (!RetrieveItemsOrderRules.IsMarkedContainer(containerItem))
            {
                return false;
            }

            if (containerItem.CurrentHull == null)
            {
                return false;
            }

            if (containerItem.GetComponent<ItemContainer>() == null)
            {
                return false;
            }

            if (containerItem.ParentInventory != null)
            {
                return false;
            }

            if (containerItem.GetComponent<Pickable>() != null)
            {
                return false;
            }

            if (Vector2.DistanceSquared(containerItem.WorldPosition, character.WorldPosition) > SearchRadius * SearchRadius)
            {
                return false;
            }

            if (ignoredTags.Any(item.HasTag))
            {
                return false;
            }

            return true;
        }

        private void RefreshIgnoredStateForItem(Item item)
        {
            if (!TryResolveTargetContainer(item, out Item containerItem))
            {
                return;
            }

            if (!RetrieveItemsOrderRules.IsMarkedContainer(containerItem))
            {
                return;
            }

            int markVersion = RetrieveItemsOrderRules.GetMarkVersion(containerItem);
            observedMarkVersions.TryGetValue(containerItem, out int observedVersion);
            if (markVersion <= observedVersion)
            {
                return;
            }

            ignoredItems.RemoveWhere(ignoredItem => IsItemInTargetContainer(ignoredItem, containerItem));
            if (lastAttemptedItem != null && IsItemInTargetContainer(lastAttemptedItem, containerItem))
            {
                lastAttemptedItem = null;
                sameTargetAttempts = 0;
            }
            observedMarkVersions[containerItem] = markVersion;
        }

        private bool IsItemInTargetContainer(Item item, Item containerItem)
        {
            if (item == null || containerItem == null)
            {
                return false;
            }

            if (item == containerItem)
            {
                return true;
            }

            return TryGetRootContainerItem(item, out Item rootContainer) && rootContainer == containerItem;
        }

        private bool IsValidMarkedPortableLoot(Item item)
        {
            if (item.Submarine != targetOutpost)
            {
                return false;
            }

            if (item.CurrentHull == null)
            {
                return false;
            }

            if (Vector2.DistanceSquared(item.WorldPosition, character.WorldPosition) > SearchRadius * SearchRadius)
            {
                return false;
            }

            if (ignoredTags.Any(item.HasTag))
            {
                return false;
            }

            return true;
        }

        private void MoveRetrievedWearableOutOfEquipSlot()
        {
            if (currentTargetItem == null ||
                currentTargetItem.Removed ||
                currentTargetItem.ParentInventory != character.Inventory ||
                currentTargetItem.GetComponent<Wearable>() == null ||
                currentTargetItem.Equipper != character)
            {
                return;
            }

            currentTargetItem.Unequip(character);
            IEnumerable<InvSlotType> anySlots =
                AccessTools.Field(typeof(CharacterInventory), "AnySlot")?.GetValue(null) as IEnumerable<InvSlotType> ??
                Enumerable.Empty<InvSlotType>();
            character.Inventory.TryPutItem(
                currentTargetItem,
                character,
                anySlots,
                false,
                true,
                false);
            RestoreInitialEquippedWearables();
        }

        private void RestoreInitialEquippedWearables()
        {
            foreach (Item item in initialEquippedWearables)
            {
                if (item == null ||
                    item.Removed ||
                    item.ParentInventory != character.Inventory ||
                    item.Equipper == character)
                {
                    continue;
                }

                item.Equip(character);
            }
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

        private bool TryResolveTargetContainer(Item item, out Item containerItem)
        {
            if (IsMarkedPortableLoot(item))
            {
                containerItem = item;
                return true;
            }

            return TryGetRootContainerItem(item, out containerItem);
        }

        private Hull GetTargetHull(Item item, Item containerItem)
        {
            if (IsMarkedPortableLoot(item))
            {
                return item.CurrentHull;
            }

            return containerItem?.CurrentHull;
        }

        private Hull GetContainerHull(Item item)
        {
            return TryResolveTargetContainer(item, out Item containerItem) ? GetTargetHull(item, containerItem) : null;
        }

        private Vector2 GetContainerPosition(Item item)
        {
            return TryResolveTargetContainer(item, out Item containerItem) ? containerItem.WorldPosition : item.WorldPosition;
        }

        private bool ShouldReturnBeforePickup(Item targetItem)
        {
            if (CountCarriedLoot() <= 0)
            {
                return false;
            }

            if (IsPortableCargo(targetItem))
            {
                return true;
            }

            int freeSlots = CountAvailableLootSlots(targetItem, includeSmallItemStorage: true);
            return freeSlots <= MinimumFreeSlotsBeforeReturn;
        }

        private bool IsPortableCargo(Item item)
        {
            return IsPortableContainerLoot(item);
        }

        private void ClearCurrentSearchTarget()
        {
            currentTargetItem = null;
            currentTargetContainer = null;
            currentTargetHull = null;
            ClearCenteringTarget();
            lastAttemptedItem = null;
            sameTargetAttempts = 0;
        }

        private void ClearCenteredSearchTarget()
        {
            centeredTargetContainer = null;
            centeredTargetHull = null;
            ClearCenteringTarget();
        }

        private void ClearCenteringTarget()
        {
            centeringTargetContainer = null;
            centeringTargetHull = null;
        }

        private bool ShouldReturnWithCurrentLoot()
        {
            List<Item> carriedLootItems = GetCarriedLoot().ToList();
            int carriedLoot = carriedLootItems.Count;
            if (carriedLoot <= 0)
            {
                return false;
            }

            if (carriedLootItems.Any(IsPortableCargo))
            {
                return true;
            }

            int freeSlots = CountAvailableLootSlots(null, includeSmallItemStorage: true);
            if (freeSlots <= MinimumFreeSlotsBeforeReturn)
            {
                return true;
            }

            // If the last pickup did not increase carried count, we are probably out of room or
            // the remaining reachable items are not practical to carry with the current inventory.
            if (IsSubObjectiveFinished() && carriedLoot <= lastCarriedCount)
            {
                return true;
            }

            return false;
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

            HashSet<Item> yielded = new HashSet<Item>();
            foreach (Item item in GetDirectInventoryItems(character.Inventory))
            {
                if (IsRetrievedLootItem(item) && yielded.Add(item))
                {
                    yield return item;
                }
            }

            foreach (Item storageItem in GetStartingMobileStorageItems())
            {
                ItemContainer itemContainer = storageItem.GetComponent<ItemContainer>();
                if (itemContainer?.Inventory == null)
                {
                    continue;
                }

                foreach (Item item in GetDirectInventoryItems(itemContainer.Inventory))
                {
                    if (IsRetrievedLootItem(item) && yielded.Add(item))
                    {
                        yield return item;
                    }
                }
            }
        }

        private bool IsRetrievedLootItem(Item item)
        {
            return item != null &&
                !item.Removed &&
                !initialInventoryItems.Contains(item) &&
                !item.HasTag(Tags.OxygenSource);
        }

        private int CountAvailableLootSlots(Item targetItem, bool includeSmallItemStorage)
        {
            int freeSlots = CountAvailableDirectSlots(character.Inventory);
            if (!includeSmallItemStorage || (targetItem != null && !targetItem.HasTag(smallItemTag)))
            {
                return freeSlots;
            }

            foreach (Item storageItem in GetStartingMobileStorageItems())
            {
                ItemContainer itemContainer = storageItem.GetComponent<ItemContainer>();
                if (itemContainer?.Inventory != null)
                {
                    freeSlots += CountAvailableDirectSlots(itemContainer.Inventory);
                }
            }

            return freeSlots;
        }

        private IEnumerable<Item> GetStartingMobileStorageItems()
        {
            if (character.Inventory == null)
            {
                yield break;
            }

            foreach (Item item in GetDirectInventoryItems(character.Inventory))
            {
                if (item == null ||
                    item.Removed ||
                    !initialInventoryItems.Contains(item) ||
                    !item.HasTag(mobileContainerTag) ||
                    item.GetComponent<ItemContainer>()?.Inventory == null)
                {
                    continue;
                }

                yield return item;
            }
        }

        private static int CountAvailableDirectSlots(object inventory)
        {
            if (inventory == null)
            {
                return 0;
            }

            int capacity = GetInventoryCapacity(inventory);
            if (capacity <= 0)
            {
                return 0;
            }

            return Math.Max(0, capacity - GetDirectInventoryItems(inventory).Count());
        }

        private static int GetInventoryCapacity(object inventory)
        {
            object capacity =
                AccessTools.Property(inventory.GetType(), "Capacity")?.GetValue(inventory) ??
                AccessTools.Field(inventory.GetType(), "Capacity")?.GetValue(inventory) ??
                AccessTools.Field(inventory.GetType(), "capacity")?.GetValue(inventory);

            return capacity is int intCapacity ? intCapacity : 0;
        }

        private void CaptureInitialInventoryItems()
        {
            initialInventoryItems.Clear();
            initialEquippedWearables.Clear();
            if (character.Inventory == null)
            {
                return;
            }

            foreach (Item item in character.Inventory.AllItems)
            {
                if (item != null && !item.Removed)
                {
                    initialInventoryItems.Add(item);
                    if (item.GetComponent<Wearable>() != null && item.Equipper == character)
                    {
                        initialEquippedWearables.Add(item);
                    }
                }
            }
        }

        private bool ShouldAbortForInjury()
        {
            if (character.IsUnconscious)
            {
                return true;
            }

            float maxVitality = Math.Max(character.MaxVitality, 1.0f);
            return character.Vitality / maxVitality <= AbandonVitalityRatio;
        }

        private bool ShouldCompleteBecauseRetrievalContextEnded()
        {
            if (IsLevelEndedOrCompleted())
            {
                return true;
            }

            if (homeSubmarine == null || targetOutpost == null)
            {
                return false;
            }

            return !(homeSubmarine.DockedTo?.Contains(targetOutpost) ?? false);
        }

        private static bool IsLevelEndedOrCompleted()
        {
            try
            {
                object gameSession =
                    AccessTools.Property(typeof(GameMain), "GameSession")?.GetValue(null) ??
                    AccessTools.Field(typeof(GameMain), "GameSession")?.GetValue(null) ??
                    AccessTools.Field(typeof(GameMain), "gameSession")?.GetValue(null);

                if (gameSession == null)
                {
                    return false;
                }

                string[] completionMembers =
                {
                    "LevelCompleted",
                    "RoundEnding",
                    "RoundEnded",
                    "IsRoundEnding",
                    "IsRoundEnded",
                    "IsLevelCompleted"
                };

                foreach (string memberName in completionMembers)
                {
                    object value =
                        AccessTools.Property(gameSession.GetType(), memberName)?.GetValue(gameSession) ??
                        AccessTools.Field(gameSession.GetType(), memberName)?.GetValue(gameSession);
                    if (value is bool isComplete && isComplete)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to inspect level completion state: {ex.Message}");
            }

            return false;
        }

        private bool IsSubObjectiveActive()
        {
            return currentSubObjective != null && !currentSubObjective.IsCompleted && !currentSubObjective.Abandon;
        }

        private bool IsSubObjectiveFinished()
        {
            return currentSubObjective != null && (currentSubObjective.IsCompleted || currentSubObjective.Abandon);
        }

        private void UpdateStuckTimer(float deltaTime)
        {
            if (Vector2.DistanceSquared(character.WorldPosition, lastWorldPosition) > StuckDistanceThreshold * StuckDistanceThreshold)
            {
                lastWorldPosition = character.WorldPosition;
                stuckTimer = 0.0f;
                return;
            }

            if (currentSubObjective != null)
            {
                stuckTimer += deltaTime;
            }
            else
            {
                stuckTimer = 0.0f;
            }
        }

        private bool IsStuckOnCurrentSubObjective()
        {
            return currentSubObjective != null && stuckTimer >= StuckTimeout;
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
            currentTargetContainer = null;
            currentTargetHull = null;
            ResetStuckTracking();
        }

        private void SetLogicStep(string step)
        {
            currentLogicStep = step;
        }

        private void FlushLogicStepLog()
        {
            if (logicLogTimer > 0.0f || string.IsNullOrWhiteSpace(currentLogicStep))
            {
                return;
            }

            // LuaCsLogger.Log($"[RetrieveItemsOrder] {currentLogicStep}");
            logicLogTimer = LogicLogInterval;
        }

        private void Speak(string message, Identifier identifier, float minDurationBetweenSimilar)
        {
            if (statusTimer > 0.0f)
            {
                return;
            }

            if (!character.IsOnPlayerTeam)
            {
                return;
            }

            character.Speak(RetrieveItemsOrderRules.GetText(identifier, message), identifier: identifier, minDurationBetweenSimilar: minDurationBetweenSimilar);
            statusTimer = minDurationBetweenSimilar;
        }
    }
}
