using System;
using System.Collections.Generic;
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
        private const float OpenWaterGridSize = 80.0f;
        private const float OpenWaterCloseEnough = 80.0f;
        private const float OpenWaterWaypointCloseEnough = 80.0f;
        private const float OpenWaterRepathInterval = 2.0f;
        private const float OpenWaterDirectGoalThreshold = 300.0f;
        private const float OpenWaterObstacleInflation = 15.0f;
        private const float OpenWaterNodeClearance = 15.0f;
        private const float OpenWaterRaycastStartClearance = 5.0f;
        private const float OpenWaterCharacterHalfWidth = 10.0f;
        private const float OpenWaterTargetItemClearance = 50.0f;
        private const int OpenWaterNearestNodeSearchRadius = 20;
        private const float OpenWaterSpatialCellSize = 500.0f;
        private float openWaterScaledClearance = OpenWaterNodeClearance;
        private List<Rectangle> openWaterScaledObstacles = null;
        private Dictionary<(int, int), List<Hull>> openWaterSpatialGrid = null;
        private int openWaterSpatialHullCount = -1;
        private Vector2 openWaterSpatialLastCenter = Vector2.Zero;
        private float openWaterLastTargetDistance = float.MaxValue;

        private void StartOpenWaterFallback()
        {
            if (!usingOpenWaterFallback)
            {
                openWaterSteering = ResolveOpenWaterSteeringManager();
                LuaCsLogger.Log($"[RetrieveItemsOrder] Wreck retrieval switching to waypointless open-water navigation for {character.Name}. hull={GetHullName(character.CurrentHull)}, inWater={character.InWater}, inHullBounds={IsCharacterInsideHullBounds(character.CurrentHull)}, pressureProtected={character.IsProtectedFromPressure}, target={currentTargetItem?.Name ?? "<null>"}, steeringManager={(ReferenceEquals(openWaterSteering, SteeringManager) ? "objective" : "outside")}");
            }

            usingOpenWaterFallback = true;
            openWaterRepathTimer = 0.0f;
            openWaterProgressTimer = 0.0f;
            openWaterMovementLogTimer = 0.0f;
            openWaterLastDistance = float.MaxValue;
            openWaterLastTargetDistance = float.MaxValue;
            openWaterScaledObstacles = null;
            openWaterSpatialGrid = null;
            openWaterSpatialHullCount = -1;
            openWaterCachedGapRects = null;
            openWaterGiveUpCount = 0;
            openWaterPath.Clear();
            openWaterPathIndex = 0;
            openWaterPathGoal = Vector2.Zero;
            ResetStuckTracking();
        }

        private void StopOpenWaterFallback()
        {
            usingOpenWaterFallback = false;
            openWaterProgressTimer = 0.0f;
            openWaterMovementLogTimer = 0.0f;
            openWaterLastDistance = float.MaxValue;
            openWaterLastTargetDistance = float.MaxValue;
            openWaterLastObstacleCount = -1;
            openWaterCachedObstacles = null;
            openWaterCachedGapRects = null;
            openWaterScaledObstacles = null;
            openWaterSpatialGrid = null;
            openWaterSpatialHullCount = -1;
            openWaterGiveUpCount = 0;
            openWaterPath.Clear();
            openWaterPathIndex = 0;
            openWaterSteering = null;
            ReleaseOpenWaterMovementControl();
        }

        private bool UpdateOpenWaterNavigation(float deltaTime, Item targetItem, float closeEnough)
        {
            if (targetItem == null || targetItem.Removed)
            {
                return false;
            }

            return UpdateOpenWaterNavigation(deltaTime, targetItem.WorldPosition, closeEnough, targetItem.Name.ToString());
        }

        private bool UpdateOpenWaterNavigation(float deltaTime, Vector2 targetWorldPosition, float closeEnough, string targetLabel)
        {
            usingOpenWaterFallback = true;
            openWaterObstacleLogTimer -= deltaTime;
            openWaterCachedObstacles = null;

            float targetDistance = Vector2.Distance(character.WorldPosition, targetWorldPosition);
            if (!IsInOpenWaterControlZone())
            {
                StopOpenWaterFallback();
                return false;
            }

            if (TryMoveOutOfExitAirlock(deltaTime, targetDistance, targetWorldPosition))
            {
                return false;
            }

            if (targetDistance <= closeEnough)
            {
                character.OverrideMovement = null;
                return true;
            }

            if (currentTargetItem != null && IsTargetInsideWreckSub() &&
                Vector2.Distance(character.WorldPosition, currentTargetItem.WorldPosition) <= SubEntryTriggerDistance + closeEnough)
            {
                StopOpenWaterFallback();
                ComputeSubEntryTargetPoint();
                travelPhase = WreckTravelPhase.SubEntryWaypoint;
                LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water navigation near wreck sub for {character.Name} (dist={targetDistance:0}); switching to sub entry waypoint");
                return false;
            }

            if (openWaterFailedRepathCount >= 3)
            {
                int failCount = openWaterFailedRepathCount;
                openWaterFailedRepathCount = 0;
                openWaterScaledClearance = OpenWaterNodeClearance;
                openWaterScaledObstacles = null;
                openWaterPath.Clear();
                openWaterPathIndex = 0;
                openWaterGiveUpCount++;
                float cooldown = Math.Min(5.0f + openWaterGiveUpCount * 10.0f, 60.0f);
                openWaterRepathTimer = cooldown;
                if (TryMoveOutOfExitAirlock(deltaTime, targetDistance, targetWorldPosition))
                {
                    ResetStuckTracking();
                    LuaCsLogger.Log($"[RetrieveItemsOrder] {character.Name} failed pathfinding {failCount} times; using airlock escape (giveUp={openWaterGiveUpCount})");
                    return false;
                }
                Vector2 awayFromTarget = Vector2.Normalize(character.WorldPosition - targetWorldPosition);
                if (awayFromTarget.LengthSquared() < 0.01f)
                {
                    awayFromTarget = new Vector2(0, 1);
                }
                character.OverrideMovement = awayFromTarget;
                ClearOpenWaterMovementInputs();
                ResetStuckTracking();
                LuaCsLogger.Log($"[RetrieveItemsOrder] {character.Name} failed pathfinding {failCount} times; pushing away from target (giveUp={openWaterGiveUpCount}, cooldown={cooldown:0}s, away=({awayFromTarget.X:0.00},{awayFromTarget.Y:0.00}))");
                return false;
            }

            openWaterRepathTimer -= deltaTime;
            List<Rectangle> currentObstacles = GetOpenWaterObstacles();
            bool obstaclesChanged = openWaterLastObstacleCount >= 0 && currentObstacles.Count != openWaterLastObstacleCount;
            openWaterLastObstacleCount = currentObstacles.Count;
            if (obstaclesChanged && openWaterRepathTimer < 2.0f)
            {
                openWaterRepathTimer = 2.0f;
                LuaCsLogger.Log($"[RetrieveItemsOrder] Obstacles changed for {character.Name}; delaying repath 2s to let physics settle");
            }
            bool shouldRepath =
                openWaterRepathTimer <= 0.0f ||
                (openWaterPath.Count > 0 && openWaterPathIndex >= openWaterPath.Count) ||
                (openWaterPath.Count > 0 && Vector2.DistanceSquared(openWaterPathGoal, targetWorldPosition) > OpenWaterGridSize * OpenWaterGridSize);
            if (shouldRepath)
            {
                openWaterPath = BuildOpenWaterPath(character.WorldPosition, targetWorldPosition);
                openWaterPathIndex = 0;
                openWaterPathGoal = targetWorldPosition;
                openWaterRepathTimer = OpenWaterRepathInterval;
                LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water path for {character.Name}: target={targetLabel}, nodes={openWaterPath.Count}, distance={targetDistance:0}, obstacles={currentObstacles.Count}, obstaclesChanged={obstaclesChanged}, directBlocked={OpenWaterSegmentBlocked(character.WorldPosition, targetWorldPosition, currentObstacles)}, steering=world-override");
                if (openWaterPath.Count == 0)
                {
                    openWaterFailedRepathCount++;
                    if (TryMoveOutOfExitAirlock(deltaTime, targetDistance, targetWorldPosition))
                    {
                        return false;
                    }

                    ReleaseOpenWaterMovementControl();
                    return false;
                }
            }

            if (openWaterPath.Count == 0)
            {
                if (TryMoveOutOfExitAirlock(deltaTime, targetDistance, targetWorldPosition))
                {
                    return false;
                }

                ReleaseOpenWaterMovementControl();
                return false;
            }

            Vector2 nextPoint = targetWorldPosition;
            while (openWaterPathIndex < openWaterPath.Count)
            {
                nextPoint = openWaterPath[openWaterPathIndex];
                float distToNodeSq = Vector2.DistanceSquared(character.WorldPosition, nextPoint);
                if (distToNodeSq > OpenWaterWaypointCloseEnough * OpenWaterWaypointCloseEnough)
                {
                    break;
                }

                if (openWaterPathIndex + 1 < openWaterPath.Count)
                {
                    List<Rectangle> obstacles = openWaterScaledObstacles ?? GetOpenWaterObstacles();
                    Vector2 followingPoint = openWaterPath[openWaterPathIndex + 1];
                    if (OpenWaterSegmentBlocked(character.WorldPosition, followingPoint, obstacles))
                    {
                        if (distToNodeSq > OpenWaterNodeClearance * OpenWaterNodeClearance)
                        {
                            break;
                        }
                    }
                }

                openWaterPathIndex++;
                openWaterFailedRepathCount = 0;
                openWaterGiveUpCount = 0;
            }

            if (openWaterPathIndex >= openWaterPath.Count)
            {
                nextPoint = targetWorldPosition;
            }

            float currentTargetDistanceSquared = Vector2.DistanceSquared(character.WorldPosition, targetWorldPosition);
            List<Rectangle> skipObstacles = openWaterScaledObstacles ?? GetOpenWaterObstacles();
            while (openWaterPathIndex < openWaterPath.Count &&
                   Vector2.DistanceSquared(nextPoint, targetWorldPosition) > currentTargetDistanceSquared + (OpenWaterGridSize * OpenWaterGridSize))
            {
                if (OpenWaterSegmentBlocked(character.WorldPosition, nextPoint, skipObstacles))
                {
                    break;
                }

                int candidateIndex = openWaterPathIndex + 1;
                if (candidateIndex >= openWaterPath.Count)
                {
                    break;
                }

                Vector2 candidatePoint = openWaterPath[candidateIndex];
                if (OpenWaterSegmentBlocked(character.WorldPosition, candidatePoint, skipObstacles))
                {
                    break;
                }

                openWaterPathIndex++;
                nextPoint = candidatePoint;
                openWaterFailedRepathCount = 0;
                openWaterGiveUpCount = 0;
            }

            if (Vector2.DistanceSquared(nextPoint, targetWorldPosition) > currentTargetDistanceSquared + (OpenWaterGridSize * OpenWaterGridSize))
            {
                nextPoint = targetWorldPosition;
            }

            float waypointDistance = Vector2.Distance(character.WorldPosition, nextPoint);
            UpdateOpenWaterProgress(deltaTime, waypointDistance, targetDistance);

            if (waypointDistance > 1.0f)
            {
                List<Rectangle> firstSegObstacles = openWaterScaledObstacles ?? GetOpenWaterObstacles();
                if (OpenWaterSegmentBlocked(character.WorldPosition, nextPoint, firstSegObstacles))
                {
                    Vector2 toNext = Vector2.Normalize(nextPoint - character.WorldPosition);
                    Vector2 perpA = new Vector2(-toNext.Y, toNext.X);
                    Vector2 perpB = new Vector2(toNext.Y, -toNext.X);
                    Vector2 reversed = -toNext;
                    Vector2 up = new Vector2(0, 1);
                    Vector2 down = new Vector2(0, -1);
                    Vector2 right = new Vector2(1, 0);
                    Vector2 left = new Vector2(-1, 0);
                    Vector2 diagNE = Vector2.Normalize(new Vector2(1, 1));
                    Vector2 diagNW = Vector2.Normalize(new Vector2(-1, 1));
                    Vector2 diagSE = Vector2.Normalize(new Vector2(1, -1));
                    Vector2 diagSW = Vector2.Normalize(new Vector2(-1, -1));
                    Vector2[] candidates = { perpA, perpB, -perpA, -perpB, reversed, up, down, right, left, diagNE, diagNW, diagSE, diagSW };
                    Vector2 slideDir = Vector2.Zero;
                    float bestScore = float.MinValue;
                    float bestTestDist = 0.0f;
                    float[] testDistances = { 5.0f, 10.0f, 20.0f, 40.0f };
                    foreach (float testDist in testDistances)
                    {
                        foreach (Vector2 dir in candidates)
                        {
                            Vector2 endPoint = character.WorldPosition + dir * testDist;
                            bool endpointBlocked = OpenWaterPhysicsSegmentBlocked(character.WorldPosition, endPoint);
                            if (endpointBlocked)
                            {
                                continue;
                            }

                            float score = Vector2.Dot(dir, toNext);
                            if (testDist > bestTestDist || (Math.Abs(testDist - bestTestDist) < 0.1f && score > bestScore))
                            {
                                bestScore = score;
                                bestTestDist = testDist;
                                slideDir = dir;
                            }
                        }
                    }

                    if (slideDir.LengthSquared() < 0.01f)
                    {
                        openWaterPath.Clear();
                        openWaterPathIndex = 0;
                        openWaterRepathTimer = 3.0f;
                        if (TryMoveOutOfExitAirlock(deltaTime, targetDistance, targetWorldPosition))
                        {
                            LuaCsLogger.Log($"[RetrieveItemsOrder] All directions blocked for {character.Name}; using airlock escape");
                            return false;
                        }
                        Vector2 pushDir = Vector2.Normalize(character.WorldPosition - targetWorldPosition);
                        if (pushDir.LengthSquared() < 0.01f)
                        {
                            pushDir = new Vector2(0, 1);
                        }
                        character.OverrideMovement = pushDir;
                        ClearOpenWaterMovementInputs();
                        ResetStuckTracking();
                        LuaCsLogger.Log($"[RetrieveItemsOrder] All directions blocked for {character.Name}; pushing away for 3s (push=({pushDir.X:0.00},{pushDir.Y:0.00}))");
                        return false;
                    }

                    ApplyOpenWaterSteering(deltaTime, slideDir * 100.0f, targetDistance, nextPoint, targetWorldPosition);
                    return false;
                }
            }

            Vector2 movement = GetOpenWaterMovementVector(nextPoint);
            if (movement.LengthSquared() < 1.0f)
            {
                movement = GetOpenWaterMovementVector(targetWorldPosition);
            }

            ApplyOpenWaterSteering(deltaTime, movement, targetDistance, nextPoint, targetWorldPosition);
            return false;
        }

        private bool TryMoveOutOfExitAirlock(float deltaTime, float targetDistance, Vector2 targetWorldPosition)
        {
            if (travelPhase != WreckTravelPhase.OpenWater ||
                exitAirlockHull == null ||
                exitAirlockGap == null ||
                !character.InWater)
            {
                return false;
            }

            bool insideOrInHullBounds =
                character.CurrentHull == exitAirlockHull ||
                IsCharacterInsideHullBounds(exitAirlockHull);
            if (!insideOrInHullBounds)
            {
                return false;
            }

            Door exitDoor = exitAirlockGap.ConnectedDoor;
            if (exitDoor == null)
            {
                return false;
            }

            if (!exitDoor.IsOpen)
            {
                ToggleDoor(exitDoor, true);
            }

            if (exitDoor.IsOpen)
            {
                Vector2 exitPoint = GetExternalExitPoint(exitAirlockHull, exitAirlockGap);
                Vector2 movement = exitPoint - character.WorldPosition;
                if (movement.LengthSquared() <= 1.0f)
                {
                    return false;
                }
                ApplyOpenWaterSteering(deltaTime, movement, targetDistance, exitPoint, targetWorldPosition);
                return true;
            }

            return false;
        }

        private Vector2 GetOpenWaterMovementVector(Vector2 nextWorldPoint)
        {
            return nextWorldPoint - character.WorldPosition;
        }

        private void ApplyOpenWaterSteering(float deltaTime, Vector2 movement, float targetDistance, Vector2 nextPoint, Vector2 targetWorldPosition)
        {
            if (movement.LengthSquared() <= 1.0f)
            {
                character.OverrideMovement = null;
                ClearOpenWaterMovementInputs();
                return;
            }

            ClearOpenWaterMovementInputs();

            if (!IsInOpenWaterControlZone())
            {
                ReleaseOpenWaterMovementControl();
                return;
            }

            Vector2 movementVector = Vector2.Normalize(movement);

            openWaterMovementLogTimer -= deltaTime;
            if (openWaterProgressTimer > 1.0f && openWaterMovementLogTimer <= 0.0f)
            {
                openWaterMovementLogTimer = 1.0f;
                Vector2 inputVector = GetOpenWaterInputVector(movementVector);
                LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water movement for {character.Name}: distance={targetDistance:0}, worldMove=({movementVector.X:0.00},{movementVector.Y:0.00}), inputMove=({inputVector.X:0.00},{inputVector.Y:0.00}), charWorld=({character.WorldPosition.X:0},{character.WorldPosition.Y:0}), waypoint=({nextPoint.X:0},{nextPoint.Y:0}), targetWorld=({targetWorldPosition.X:0},{targetWorldPosition.Y:0}), hull={GetHullName(character.CurrentHull)}, inHullBounds={IsCharacterInsideHullBounds(character.CurrentHull)}");
            }

            ApplyOpenWaterMovementInputs(deltaTime, movementVector);
        }

        private void ApplyOpenWaterMovementInputs(float deltaTime, Vector2 movementVector)
        {
            character.OverrideMovement = movementVector;
            try
            {
                HumanAIController.Steering = movementVector;
                openWaterSteering?.SteeringManual(deltaTime, movementVector);
                SteeringManager?.SteeringManual(deltaTime, movementVector);
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to apply open-water AI steering for {character.Name}: {ex.Message}");
            }

            Vector2 inputVector = GetOpenWaterInputVector(movementVector);
            const float inputThreshold = 0.15f;
            character.SetInput(InputType.Left, inputVector.X < -inputThreshold, false);
            character.SetInput(InputType.Right, inputVector.X > inputThreshold, false);
            character.SetInput(InputType.Up, inputVector.Y > inputThreshold, false);
            character.SetInput(InputType.Down, inputVector.Y < -inputThreshold, false);
        }

        private Vector2 GetOpenWaterInputVector(Vector2 worldMovementVector)
        {
            return worldMovementVector;
        }

        private bool IsInOpenWaterControlZone()
        {
            Hull currentHull = character.CurrentHull;
            if (currentHull == null)
            {
                return true;
            }

            return character.InWater && !IsCharacterInsideHullBounds(currentHull);
        }

        private bool IsCharacterInsideHullBounds(Hull hull)
        {
            return IsWorldPointInsideHullBounds(hull, character.WorldPosition);
        }

        private bool IsWorldPointInsideHullBounds(Hull hull, Vector2 worldPosition)
        {
            if (hull == null)
            {
                return false;
            }

            Rectangle rect = GetWorldRect(hull);
            return rect.Width > 0 &&
                   rect.Height > 0 &&
                   rect.Contains((int)worldPosition.X, (int)worldPosition.Y);
        }

        private void ReleaseOpenWaterMovementControl()
        {
            character.OverrideMovement = null;
            ClearOpenWaterMovementInputs();
        }

        private SteeringManager ResolveOpenWaterSteeringManager()
        {
            try
            {
                object outsideSteering =
                    AccessTools.Field(HumanAIController?.GetType(), "outsideSteering")?.GetValue(HumanAIController) ??
                    AccessTools.Property(HumanAIController?.GetType(), "OutsideSteering")?.GetValue(HumanAIController);
                if (outsideSteering is SteeringManager steering)
                {
                    return steering;
                }
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to resolve outside steering manager for {character.Name}: {ex.Message}");
            }

            return SteeringManager;
        }

        private void ClearOpenWaterMovementInputs()
        {
            character.SetInput(InputType.Left, false, false);
            character.SetInput(InputType.Right, false, false);
            character.SetInput(InputType.Up, false, false);
            character.SetInput(InputType.Down, false, false);
        }

        private void UpdateOpenWaterProgress(float deltaTime, float waypointDistance, float targetDistance)
        {
            if (openWaterLastDistance == float.MaxValue || waypointDistance < openWaterLastDistance - 8.0f)
            {
                openWaterLastDistance = waypointDistance;
                openWaterLastTargetDistance = targetDistance;
                openWaterProgressTimer = 0.0f;
                return;
            }

            if (targetDistance < openWaterLastTargetDistance - 20.0f)
            {
                openWaterLastDistance = waypointDistance;
                openWaterLastTargetDistance = targetDistance;
                openWaterProgressTimer = 0.0f;
                return;
            }

            if (waypointDistance > openWaterLastDistance + 20.0f || Math.Abs(waypointDistance - openWaterLastDistance) < 4.0f)
            {
                openWaterProgressTimer += deltaTime;
            }
            else
            {
                openWaterProgressTimer = 0.0f;
            }

            if (openWaterProgressTimer >= 4.0f)
            {
                openWaterProgressTimer = 0.0f;
                openWaterLastDistance = waypointDistance;
                openWaterLastTargetDistance = targetDistance;
                openWaterRepathTimer = 0.0f;
                openWaterPath.Clear();
                openWaterPathIndex = 0;
                LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water navigation made no progress for {character.Name}; forcing repath, distance={targetDistance:0}, waypointDistance={waypointDistance:0}, steering=world-override");
            }
        }

        private List<Vector2> BuildOpenWaterPath(Vector2 start, Vector2 goal)
        {
            openWaterCachedGapRects = null;
            List<Rectangle> nearbyObstacles = GetNearbyObstacles(start, goal, OpenWaterObstacleInflation);
            if (!OpenWaterSegmentBlocked(start, goal, nearbyObstacles))
            {
                return new List<Vector2> { start, goal };
            }

            Vector2 startAnchor = GetOpenWaterStartAnchor(start);
            Rectangle bounds = GetOpenWaterSearchBounds(startAnchor, goal);
            int minDim = Math.Min(bounds.Width, bounds.Height);
            float clearanceScale = minDim <= 6 ? 0.50f :
                                   minDim <= 12 ? 0.60f :
                                   minDim <= 25 ? 0.70f :
                                   minDim <= 40 ? 0.85f :
                                   1.0f;
            openWaterScaledClearance = OpenWaterNodeClearance * clearanceScale;
            float scaledInflation = OpenWaterObstacleInflation * clearanceScale;
            List<Rectangle> tightObstacles = clearanceScale < 1.0f
                ? GetNearbyObstacles(start, goal, scaledInflation)
                : nearbyObstacles;
            LuaCsLogger.Log($"[RetrieveItemsOrder] Scaled clearance for {character.Name}: bounds=({bounds.Width},{bounds.Height}), minDim={minDim}, clearance={openWaterScaledClearance:0}, inflation={scaledInflation:0}, nearbyObstacles={nearbyObstacles.Count}/{GetOpenWaterObstacles().Count}");
            for (int i = 0; i < tightObstacles.Count; i++)
            {
                Rectangle r = tightObstacles[i];
                LuaCsLogger.Log($"[RetrieveItemsOrder]   nearby[{i}]: pos=({r.X},{r.Y}), size=({r.Width},{r.Height}), center=({r.X + r.Width / 2},{r.Y + r.Height / 2})");
            }

            openWaterScaledObstacles = tightObstacles;
            List<Vector2> primaryResult = TryBuildPathWithGrid(start, goal, startAnchor, bounds, tightObstacles, OpenWaterGridSize);
            if (primaryResult != null)
            {
                return PrependStartPosition(primaryResult, start);
            }

            float distance = Vector2.Distance(start, goal);
            if (distance < OpenWaterGridSize * 8.0f)
            {
                float fineGrid = OpenWaterGridSize * 0.5f;
                Rectangle fineBounds = GetOpenWaterSearchBounds(startAnchor, goal, fineGrid);
                LuaCsLogger.Log($"[RetrieveItemsOrder] Retrying path with fine grid ({fineGrid:0}) for {character.Name}");
                List<Vector2> fineResult = TryBuildPathWithGrid(start, goal, startAnchor, fineBounds, tightObstacles, fineGrid);
                if (fineResult != null)
                {
                    return PrependStartPosition(fineResult, start);
                }
            }

            LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water path exhausted for {character.Name}: bounds=({bounds.X},{bounds.Y},{bounds.Width},{bounds.Height})");

            float savedClearance = openWaterScaledClearance;
            float relaxedInflation = Math.Max(scaledInflation * 0.6f, 5.0f);
            float relaxedClearance = OpenWaterNodeClearance * 0.5f;
            openWaterScaledClearance = relaxedClearance;
            List<Rectangle> relaxedObstacles = GetNearbyObstacles(start, goal, relaxedInflation);
            LuaCsLogger.Log($"[RetrieveItemsOrder] Trying relaxed fallback for {character.Name}: relaxedInflation={relaxedInflation:0}, relaxedClearance={relaxedClearance:0}, relaxedObstacles={relaxedObstacles.Count}");
            List<Vector2> relaxedResult = TryBuildPathWithGrid(start, goal, startAnchor, bounds, relaxedObstacles, OpenWaterGridSize);
            if (relaxedResult != null)
            {
                openWaterScaledObstacles = relaxedObstacles;
                return PrependStartPosition(relaxedResult, start);
            }

            openWaterScaledClearance = savedClearance;
            return new List<Vector2>();
        }

        private List<Vector2> PrependStartPosition(List<Vector2> path, Vector2 startPosition)
        {
            if (path.Count > 0 && Vector2.DistanceSquared(path[0], startPosition) > 25.0f * 25.0f)
            {
                path.Insert(0, startPosition);
            }

            return path;
        }

        private List<Vector2> TryBuildPathWithGrid(Vector2 start, Vector2 goal, Vector2 startAnchor, Rectangle bounds, List<Rectangle> obstacles, float gridSize)
        {
            Point preferredStartNode = WorldToOpenWaterNode(startAnchor, gridSize);
            Point preferredGoalNode = WorldToOpenWaterNode(goal, gridSize);
            Point? resolvedStartNode = FindNearestOpenWaterNode(preferredStartNode, startAnchor, obstacles, bounds, gridSize);
            Point? resolvedGoalNode = FindNearestOpenWaterNode(preferredGoalNode, goal, obstacles, bounds, gridSize);
            if (resolvedStartNode == null || resolvedGoalNode == null)
            {
                return null;
            }

            Point startNode = resolvedStartNode.Value;
            Point goalNode = resolvedGoalNode.Value;
            if (startNode == goalNode)
            {
                return OpenWaterSegmentBlocked(start, goal, obstacles)
                    ? null
                    : new List<Vector2> { start, goal };
            }

            Dictionary<Point, Point> cameFrom = new Dictionary<Point, Point>();
            Dictionary<Point, float> costSoFar = new Dictionary<Point, float>();
            OpenWaterMinHeap open = new OpenWaterMinHeap(256);
            HashSet<Point> closed = new HashSet<Point>();
            costSoFar[startNode] = 0.0f;
            open.Enqueue(startNode, OpenWaterHeuristic(startNode, goalNode));

            const int maxExplored = 3000;
            while (open.Count > 0)
            {
                Point current = open.Dequeue();

                if (current == goalNode)
                {
                    List<Vector2> rawPath = ReconstructOpenWaterPath(cameFrom, current, gridSize);
                    return SmoothOpenWaterPath(rawPath, obstacles);
                }

                closed.Add(current);
                if (closed.Count >= maxExplored)
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] A* node limit reached (grid={gridSize:0}) for {character.Name}: explored={closed.Count}");
                    break;
                }
                foreach (Point next in GetOpenWaterNeighbors(current))
                {
                    Vector2 currentWorld = OpenWaterNodeToWorld(current, gridSize);
                    Vector2 nextWorld = OpenWaterNodeToWorld(next, gridSize);
                    if (closed.Contains(next) ||
                        !bounds.Contains(next.X, next.Y) ||
                        OpenWaterNodeBlocked(next, obstacles, gridSize) ||
                        OpenWaterSegmentBlocked(currentWorld, nextWorld, obstacles))
                    {
                        continue;
                    }

                    float newCost = costSoFar[current] + OpenWaterStepCost(current, next);
                    if (!costSoFar.TryGetValue(next, out float existingCost) || newCost < existingCost)
                    {
                        costSoFar[next] = newCost;
                        cameFrom[next] = current;
                        float priority = newCost + OpenWaterHeuristic(next, goalNode);
                        if (open.Contains(next))
                        {
                            open.UpdatePriority(next, priority);
                        }
                        else
                        {
                            open.Enqueue(next, priority);
                        }
                    }
                }
            }

            LuaCsLogger.Log($"[RetrieveItemsOrder] A* exhausted (grid={gridSize:0}) for {character.Name}: explored={closed.Count}");
            return null;
        }

        private Vector2 GetOpenWaterStartAnchor(Vector2 start)
        {
            if (travelPhase != WreckTravelPhase.OpenWater ||
                exitAirlockHull == null ||
                exitAirlockGap == null)
            {
                return start;
            }

            bool nearExitAirlock =
                character.CurrentHull == exitAirlockHull ||
                IsCharacterInsideHullBounds(exitAirlockHull) ||
                Vector2.DistanceSquared(start, GetGapCenter(exitAirlockGap)) < 900.0f * 900.0f;
            if (!nearExitAirlock)
            {
                return start;
            }

            return GetExternalExitPoint(exitAirlockHull, exitAirlockGap);
        }

        private List<Rectangle> GetOpenWaterObstacles()
        {
            if (openWaterCachedObstacles != null)
            {
                return openWaterCachedObstacles;
            }

            openWaterCachedObstacles = GetOpenWaterObstaclesWithInflation(OpenWaterObstacleInflation);
            return openWaterCachedObstacles;
        }

        private List<Rectangle> GetOpenWaterObstaclesWithInflation(float inflation)
        {
            Hull currentHull = character.CurrentHull;
            Hull targetHull = currentTargetItem?.CurrentHull;
            bool characterInsideCurrentHull = IsCharacterInsideHullBounds(currentHull);
            bool targetInsideTargetHull = IsWorldPointInsideHullBounds(targetHull, currentTargetItem?.WorldPosition ?? Vector2.Zero);
            return Hull.HullList
                .Where(hull =>
                    hull != null &&
                    (travelPhase != WreckTravelPhase.OpenWater || hull != exitAirlockHull) &&
                    (hull != currentHull || !characterInsideCurrentHull) &&
                    (hull != targetHull || !targetInsideTargetHull))
                .Select(h => GetInflatedHullWorldRect(h, inflation))
                .Where(rect => rect.Width > 0 && rect.Height > 0)
                .ToList();
        }

        private List<Rectangle> GetNearbyObstacles(Vector2 start, Vector2 goal, float inflation)
        {
            float dist = Vector2.Distance(start, goal);
            float radius = Math.Max(dist * 1.5f, 400.0f) + inflation;
            float radiusSq = radius * radius;
            Vector2 center = (start + goal) * 0.5f;
            Hull currentHull = character.CurrentHull;
            Hull targetHull = currentTargetItem?.CurrentHull;
            bool characterInsideCurrentHull = IsCharacterInsideHullBounds(currentHull);
            bool targetInsideTargetHull = IsWorldPointInsideHullBounds(targetHull, currentTargetItem?.WorldPosition ?? Vector2.Zero);

            BuildSpatialGridIfNeeded();

            int minCellX = (int)Math.Floor((center.X - radius) / OpenWaterSpatialCellSize);
            int maxCellX = (int)Math.Ceiling((center.X + radius) / OpenWaterSpatialCellSize);
            int minCellY = (int)Math.Floor((center.Y - radius) / OpenWaterSpatialCellSize);
            int maxCellY = (int)Math.Ceiling((center.Y + radius) / OpenWaterSpatialCellSize);

            List<Rectangle> nearby = new List<Rectangle>();
            for (int cx = minCellX; cx <= maxCellX; cx++)
            {
                for (int cy = minCellY; cy <= maxCellY; cy++)
                {
                    if (!openWaterSpatialGrid.TryGetValue((cx, cy), out List<Hull> cell))
                    {
                        continue;
                    }

                    foreach (Hull hull in cell)
                    {
                        if (hull == null ||
                            (travelPhase == WreckTravelPhase.OpenWater && hull == exitAirlockHull) ||
                            (hull == currentHull && characterInsideCurrentHull) ||
                            (hull == targetHull && targetInsideTargetHull))
                        {
                            continue;
                        }

                        Rectangle rect = GetWorldRect(hull);
                        float hx = rect.X + rect.Width * 0.5f;
                        float hy = rect.Y + rect.Height * 0.5f;
                        float dx = hx - center.X;
                        float dy = hy - center.Y;
                        if (dx * dx + dy * dy > radiusSq)
                        {
                            continue;
                        }

                        Rectangle inflated = GetInflatedHullWorldRect(hull, inflation);
                        if (inflated.Width > 0 && inflated.Height > 0)
                        {
                            nearby.Add(inflated);
                        }
                    }
                }
            }

            return nearby;
        }

        private void BuildSpatialGridIfNeeded()
        {
            int currentCount = Hull.HullList?.Count ?? 0;
            if (openWaterSpatialGrid != null && openWaterSpatialHullCount == currentCount)
            {
                return;
            }

            openWaterSpatialGrid = new Dictionary<(int, int), List<Hull>>();
            if (Hull.HullList == null)
            {
                openWaterSpatialHullCount = 0;
                return;
            }

            foreach (Hull hull in Hull.HullList)
            {
                if (hull == null)
                {
                    continue;
                }

                Rectangle rect = GetWorldRect(hull);
                int cellX = (int)Math.Floor((rect.X + rect.Width * 0.5f) / OpenWaterSpatialCellSize);
                int cellY = (int)Math.Floor((rect.Y + rect.Height * 0.5f) / OpenWaterSpatialCellSize);
                var key = (cellX, cellY);
                if (!openWaterSpatialGrid.TryGetValue(key, out List<Hull> cell))
                {
                    cell = new List<Hull>();
                    openWaterSpatialGrid[key] = cell;
                }

                cell.Add(hull);
            }

            openWaterSpatialHullCount = currentCount;
        }

        private List<Vector2> GetSimpleFallbackPath(Vector2 start, Vector2 goal, List<Rectangle> obstacles)
        {
            return OpenWaterSegmentBlocked(start, goal, obstacles)
                ? new List<Vector2>()
                : new List<Vector2> { start, goal };
        }

        private List<Vector2> GetFallbackOpenWaterPath(Vector2 start, Vector2 goal, List<Rectangle> obstacles, Point failedStartNode, Rectangle failedBounds)
        {
            if (!OpenWaterSegmentBlocked(start, goal, obstacles))
            {
                return new List<Vector2> { start, goal };
            }

            Rectangle expandedBounds = new Rectangle(
                failedBounds.X - failedBounds.Width / 2,
                failedBounds.Y - failedBounds.Height / 2,
                failedBounds.Width * 2,
                failedBounds.Height * 2);
            Point? expandedStart = FindNearestOpenWaterNode(failedStartNode, start, obstacles, expandedBounds);
            Point? expandedGoal = FindNearestOpenWaterNode(WorldToOpenWaterNode(goal), goal, obstacles, expandedBounds);
            if (expandedStart != null && expandedGoal != null && expandedStart.Value != expandedGoal.Value)
            {
                List<Vector2> expandedPath = TryAStarPath(expandedStart.Value, expandedGoal.Value, obstacles, expandedBounds);
                if (expandedPath.Count > 0)
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water expanded fallback path found for {character.Name}: nodes={expandedPath.Count}");
                    return PrependStartPosition(expandedPath, start);
                }
            }

            Point nearestToStart = WorldToOpenWaterNode(start);
            List<Rectangle> gapRects = GetOpenWaterPassableGapRects();
            Vector2 bestCandidate = Vector2.Zero;
            float bestCandidateScore = float.MinValue;
            for (int radius = 1; radius <= 20; radius++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    for (int y = -radius; y <= radius; y++)
                    {
                        if (Math.Abs(x) != radius && Math.Abs(y) != radius)
                        {
                            continue;
                        }

                        Point candidate = new Point(nearestToStart.X + x, nearestToStart.Y + y);
                        Vector2 candidateWorld = OpenWaterNodeToWorld(candidate);
                        if (OpenWaterRectangleObstacleBlocked(candidateWorld, obstacles, gapRects) ||
                            OpenWaterPhysicsPointBlocked(candidateWorld))
                        {
                            continue;
                        }

                        if (!OpenWaterSegmentBlocked(start, candidateWorld, obstacles))
                        {
                            Vector2 toGoal = Vector2.Normalize(goal - start);
                            Vector2 offset = candidateWorld - start;
                            float alongDir = Vector2.Dot(offset, toGoal);
                            float distSq = offset.LengthSquared();
                            float score = alongDir - distSq * 0.00001f;
                            if (score > bestCandidateScore)
                            {
                                bestCandidateScore = score;
                                bestCandidate = candidateWorld;
                            }
                        }
                    }
                }

                if (bestCandidate.LengthSquared() > 0.01f && radius >= 5)
                {
                    break;
                }
            }

            if (bestCandidate.LengthSquared() > 0.01f)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water nearest-node fallback for {character.Name}: toward=({bestCandidate.X:0},{bestCandidate.Y:0})");
                return new List<Vector2> { start, bestCandidate, goal };
            }

            LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water fallback failed for {character.Name}; returning empty path");
            return new List<Vector2>();
        }

        private List<Vector2> TryAStarPath(Point startNode, Point goalNode, List<Rectangle> obstacles, Rectangle bounds)
        {
            Dictionary<Point, Point> cameFrom = new Dictionary<Point, Point>();
            Dictionary<Point, float> costSoFar = new Dictionary<Point, float>();
            OpenWaterMinHeap open = new OpenWaterMinHeap(128);
            HashSet<Point> closed = new HashSet<Point>();
            costSoFar[startNode] = 0.0f;
            open.Enqueue(startNode, OpenWaterHeuristic(startNode, goalNode));

            const int maxExplored = 3000;
            while (open.Count > 0)
            {
                Point current = open.Dequeue();
                if (current == goalNode)
                {
                    return SmoothOpenWaterPath(ReconstructOpenWaterPath(cameFrom, current), obstacles);
                }

                closed.Add(current);
                if (closed.Count >= maxExplored)
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] A* fine-grid node limit reached for {character.Name}: explored={closed.Count}");
                    break;
                }
                foreach (Point next in GetOpenWaterNeighbors(current))
                {
                    Vector2 currentWorld = OpenWaterNodeToWorld(current);
                    Vector2 nextWorld = OpenWaterNodeToWorld(next);
                    if (closed.Contains(next) ||
                        !bounds.Contains(next.X, next.Y) ||
                        OpenWaterNodeBlocked(next, obstacles) ||
                        OpenWaterSegmentBlocked(currentWorld, nextWorld, obstacles))
                    {
                        continue;
                    }

                    float newCost = costSoFar[current] + OpenWaterStepCost(current, next);
                    if (!costSoFar.TryGetValue(next, out float existingCost) || newCost < existingCost)
                    {
                        costSoFar[next] = newCost;
                        cameFrom[next] = current;
                        float priority = newCost + OpenWaterHeuristic(next, goalNode);
                        if (open.Contains(next))
                        {
                            open.UpdatePriority(next, priority);
                        }
                        else
                        {
                            open.Enqueue(next, priority);
                        }
                    }
                }
            }

            return new List<Vector2>();
        }

        private Rectangle GetOpenWaterSearchBounds(Vector2 start, Vector2 goal)
        {
            return GetOpenWaterSearchBounds(start, goal, OpenWaterGridSize);
        }

        private Rectangle GetOpenWaterSearchBounds(Vector2 start, Vector2 goal, float gridSize)
        {
            float dist = Vector2.Distance(start, goal);
            int margin = (int)Math.Max(gridSize * 18.0f, dist * 2.0f);
            int minX = (int)Math.Floor(Math.Min(start.X, goal.X) - margin);
            int minY = (int)Math.Floor(Math.Min(start.Y, goal.Y) - margin);
            int maxX = (int)Math.Ceiling(Math.Max(start.X, goal.X) + margin);
            int maxY = (int)Math.Ceiling(Math.Max(start.Y, goal.Y) + margin);
            Point min = WorldToOpenWaterNode(new Vector2(minX, minY), gridSize);
            Point max = WorldToOpenWaterNode(new Vector2(maxX, maxY), gridSize);
            return new Rectangle(min.X, min.Y, Math.Max(max.X - min.X, 1), Math.Max(max.Y - min.Y, 1));
        }

        private Point? FindNearestOpenWaterNode(Point preferredNode, Vector2 preferredWorldPosition, List<Rectangle> obstacles, Rectangle bounds)
        {
            return FindNearestOpenWaterNode(preferredNode, preferredWorldPosition, obstacles, bounds, OpenWaterGridSize);
        }

        private Point? FindNearestOpenWaterNode(Point preferredNode, Vector2 preferredWorldPosition, List<Rectangle> obstacles, Rectangle bounds, float gridSize)
        {
            if (bounds.Contains(preferredNode.X, preferredNode.Y) && !OpenWaterNodeBlocked(preferredNode, obstacles, gridSize))
            {
                return preferredNode;
            }

            Point? bestNode = null;
            float bestDistanceSquared = float.MaxValue;
            for (int radius = 1; radius <= OpenWaterNearestNodeSearchRadius; radius++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    for (int y = -radius; y <= radius; y++)
                    {
                        if (Math.Abs(x) != radius && Math.Abs(y) != radius)
                        {
                            continue;
                        }

                        Point candidate = new Point(preferredNode.X + x, preferredNode.Y + y);
                        Vector2 candidateWorld = OpenWaterNodeToWorld(candidate, gridSize);
                        if (!bounds.Contains(candidate.X, candidate.Y) ||
                            OpenWaterNodeBlocked(candidate, obstacles, gridSize))
                        {
                            continue;
                        }

                        float distanceSquared = Vector2.DistanceSquared(candidateWorld, preferredWorldPosition);
                        if (distanceSquared < bestDistanceSquared)
                        {
                            bestDistanceSquared = distanceSquared;
                            bestNode = candidate;
                        }
                    }
                }

                if (bestNode != null)
                {
                    return bestNode;
                }
            }

            return null;
        }

        private static IEnumerable<Point> GetOpenWaterNeighbors(Point node)
        {
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    if (x == 0 && y == 0)
                    {
                        continue;
                    }

                    yield return new Point(node.X + x, node.Y + y);
                }
            }
        }

        private static float OpenWaterHeuristic(Point a, Point b)
        {
            int dx = a.X - b.X;
            int dy = a.Y - b.Y;
            return (float)Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static float OpenWaterStepCost(Point a, Point b)
        {
            return a.X != b.X && a.Y != b.Y ? 1.4142f : 1.0f;
        }

        private List<Vector2> ReconstructOpenWaterPath(Dictionary<Point, Point> cameFrom, Point current)
        {
            return ReconstructOpenWaterPath(cameFrom, current, OpenWaterGridSize);
        }

        private List<Vector2> ReconstructOpenWaterPath(Dictionary<Point, Point> cameFrom, Point current, float gridSize)
        {
            List<Vector2> path = new List<Vector2> { OpenWaterNodeToWorld(current, gridSize) };
            while (cameFrom.TryGetValue(current, out Point previous))
            {
                current = previous;
                path.Add(OpenWaterNodeToWorld(current, gridSize));
            }

            path.Reverse();
            return path;
        }

        private List<Vector2> SmoothOpenWaterPath(List<Vector2> path, List<Rectangle> obstacles)
        {
            if (path.Count <= 2)
            {
                return path;
            }

            List<Vector2> smoothed = new List<Vector2> { path[0] };
            int anchor = 0;
            while (anchor < path.Count - 1)
            {
                int next = anchor + 1;
                while (next < path.Count - 1 && !OpenWaterSegmentBlocked(path[anchor], path[next + 1], obstacles))
                {
                    next++;
                }

                smoothed.Add(path[next]);
                anchor = next;
            }

            return RefineOpenWaterPath(smoothed, obstacles);
        }

        private List<Vector2> RefineOpenWaterPath(List<Vector2> path, List<Rectangle> obstacles)
        {
            if (path.Count <= 2)
            {
                return path;
            }

            List<Rectangle> gapRects = GetOpenWaterPassableGapRects();
            float requiredClearance = Math.Max(openWaterScaledClearance, OpenWaterCharacterHalfWidth);
            List<Vector2> refined = new List<Vector2>(path);
            bool changed = true;
            int iterations = 0;
            while (changed && iterations < 3)
            {
                changed = false;
                iterations++;
                for (int i = 1; i < refined.Count - 1; i++)
                {
                    Vector2 waypoint = refined[i];
                    Vector2 prev = refined[i - 1];
                    Vector2 next = refined[i + 1];
                    Vector2 segDirA = waypoint - prev;
                    Vector2 segDirB = next - waypoint;
                    Vector2 segNormal = Vector2.Zero;
                    if (segDirA.LengthSquared() > 0.01f && segDirB.LengthSquared() > 0.01f)
                    {
                        Vector2 avgDir = Vector2.Normalize(segDirA) + Vector2.Normalize(segDirB);
                        if (avgDir.LengthSquared() > 0.01f)
                        {
                            segNormal = new Vector2(-avgDir.Y, avgDir.X);
                        }
                    }

                    if (segNormal.LengthSquared() < 0.01f)
                    {
                        if (segDirA.LengthSquared() > 0.01f)
                        {
                            segNormal = new Vector2(-segDirA.Y, segDirA.X);
                        }
                        else if (segDirB.LengthSquared() > 0.01f)
                        {
                            segNormal = new Vector2(-segDirB.Y, segDirB.X);
                        }
                    }

                    Vector2 bestPush = Vector2.Zero;
                    float bestPushDist = float.MaxValue;
                    int wx = (int)waypoint.X;
                    int wy = (int)waypoint.Y;
                    foreach (Rectangle rect in obstacles)
                    {
                        if (gapRects.Any(g => g.Contains(wx, wy)))
                        {
                            continue;
                        }

                        float closestX = Math.Max(rect.X, Math.Min(waypoint.X, rect.X + rect.Width));
                        float closestY = Math.Max(rect.Y, Math.Min(waypoint.Y, rect.Y + rect.Height));
                        Vector2 closestPoint = new Vector2(closestX, closestY);
                        float dist = Vector2.Distance(waypoint, closestPoint);
                        if (dist >= requiredClearance)
                        {
                            continue;
                        }

                        Vector2 awayFromRect = waypoint - closestPoint;
                        if (awayFromRect.LengthSquared() < 0.01f)
                        {
                            awayFromRect = segNormal;
                        }

                        if (awayFromRect.LengthSquared() < 0.01f)
                        {
                            continue;
                        }

                        awayFromRect = Vector2.Normalize(awayFromRect);
                        float needed = requiredClearance - dist;
                        if (needed < bestPushDist)
                        {
                            bestPushDist = needed;
                            bestPush = awayFromRect;
                        }
                    }

                    if (bestPush.LengthSquared() > 0.01f && bestPushDist > 0.1f)
                    {
                        Vector2 newWaypoint = waypoint + bestPush * bestPushDist;
                        if (!OpenWaterSegmentBlocked(prev, newWaypoint, obstacles) &&
                            !OpenWaterSegmentBlocked(newWaypoint, next, obstacles))
                        {
                            refined[i] = newWaypoint;
                            changed = true;
                        }
                    }
                }
            }

            return refined;
        }

        private bool OpenWaterNodeBlocked(Point node, List<Rectangle> obstacles)
        {
            return OpenWaterNodeBlocked(node, obstacles, OpenWaterGridSize);
        }

        private bool OpenWaterNodeBlocked(Point node, List<Rectangle> obstacles, float gridSize)
        {
            Vector2 world = OpenWaterNodeToWorld(node, gridSize);
            List<Rectangle> gapRects = GetOpenWaterPassableGapRects();
            if (OpenWaterRectangleObstacleBlocked(world, obstacles, gapRects))
            {
                return true;
            }

            return OpenWaterPhysicsPointBlocked(world);
        }

        private bool OpenWaterSegmentBlocked(Vector2 start, Vector2 end, List<Rectangle> obstacles)
        {
            List<Rectangle> gapRects = GetOpenWaterPassableGapRects();
            if (OpenWaterSegmentBlockedByRectangles(start, end, obstacles, gapRects))
            {
                return true;
            }

            if (OpenWaterPhysicsSegmentBlocked(start, end))
            {
                return true;
            }

            Vector2 direction = end - start;
            float distance = direction.Length();
            if (distance > 1.0f)
            {
                float halfWidth = OpenWaterCharacterHalfWidth;
                Vector2 perpendicular = new Vector2(-direction.Y, direction.X) * (halfWidth / distance);
                if (perpendicular.LengthSquared() > 0.01f)
                {
                    if (OpenWaterPhysicsSegmentBlocked(start + perpendicular, end + perpendicular) ||
                        OpenWaterPhysicsSegmentBlocked(start - perpendicular, end - perpendicular))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool OpenWaterSegmentBlockedByRectangles(Vector2 start, Vector2 end, List<Rectangle> obstacles, List<Rectangle> passableGapRects)
        {
            Vector2 direction = end - start;
            float distance = direction.Length();
            int steps = Math.Max((int)(distance / (OpenWaterGridSize * 0.5f)), 1);

            Vector2 perpendicular = Vector2.Zero;
            if (distance > 1.0f)
            {
                float halfWidth = OpenWaterCharacterHalfWidth;
                perpendicular = new Vector2(-direction.Y, direction.X) * (halfWidth / distance);
            }

            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                Vector2 point = Vector2.Lerp(start, end, t);
                if (OpenWaterRectangleObstacleBlocked(point, obstacles, passableGapRects))
                {
                    return true;
                }

                if (perpendicular.LengthSquared() > 0.01f)
                {
                    Vector2 leftPoint = point + perpendicular;
                    Vector2 rightPoint = point - perpendicular;
                    if (OpenWaterRectangleObstacleBlocked(leftPoint, obstacles, passableGapRects) ||
                        OpenWaterRectangleObstacleBlocked(rightPoint, obstacles, passableGapRects))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool OpenWaterRectangleObstacleBlocked(Vector2 world, List<Rectangle> obstacles, List<Rectangle> passableGapRects)
        {
            int x = (int)world.X;
            int y = (int)world.Y;
            if (passableGapRects.Any(rect => rect.Contains(x, y)))
            {
                return false;
            }

            return obstacles.Any(rect => rect.Contains(x, y));
        }

        private bool OpenWaterPhysicsPointBlocked(Vector2 world)
        {
            float probeDist = OpenWaterRaycastStartClearance + 2.0f;
            Vector2 hStart = world + new Vector2(-probeDist, 0.0f);
            Vector2 hEnd = world + new Vector2(probeDist, 0.0f);
            if (OpenWaterPhysicsSegmentBlocked(hStart, hEnd))
            {
                return true;
            }

            Vector2 vStart = world + new Vector2(0.0f, -probeDist);
            Vector2 vEnd = world + new Vector2(0.0f, probeDist);
            if (OpenWaterPhysicsSegmentBlocked(vStart, vEnd))
            {
                return true;
            }

            Vector2 diagNE = world + new Vector2(probeDist, probeDist);
            if (OpenWaterPhysicsSegmentBlocked(world - new Vector2(probeDist, probeDist), diagNE))
            {
                return true;
            }

            Vector2 diagSE = world + new Vector2(probeDist, -probeDist);
            return OpenWaterPhysicsSegmentBlocked(world - new Vector2(probeDist, -probeDist), diagSE);
        }

        private bool OpenWaterPhysicsSegmentBlocked(Vector2 start, Vector2 end)
        {
            if (GameMain.World == null || Vector2.DistanceSquared(start, end) < 1.0f)
            {
                return false;
            }

            try
            {
                bool blocked = false;
                List<Rectangle> passableGapRects = GetOpenWaterPassableGapRects();
                Vector2 simStart = ConvertUnits.ToSimUnits(start);
                Vector2 simEnd = ConvertUnits.ToSimUnits(end);

                GameMain.World.RayCast((fixture, point, normal, fraction) =>
                {
                    Vector2 hitWorld = ConvertUnits.ToDisplayUnits(point);
                    if (ShouldIgnoreOpenWaterRaycastHit(fixture, hitWorld, start, passableGapRects))
                    {
                        return -1.0f;
                    }

                    blocked = true;
                    LogOpenWaterRaycastHit(fixture, hitWorld, start, end, normal, fraction);
                    return 0.0f;
                }, simStart, simEnd, Category.All);

                return blocked;
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water physics obstacle check failed: {ex.Message}");
                return false;
            }
        }

        private bool ShouldIgnoreOpenWaterRaycastHit(Fixture fixture, Vector2 hitWorld, Vector2 startWorld, List<Rectangle> passableGapRects)
        {
            if (fixture == null || fixture.Body == null)
            {
                return true;
            }

            if (Vector2.DistanceSquared(hitWorld, startWorld) < OpenWaterRaycastStartClearance * OpenWaterRaycastStartClearance)
            {
                return true;
            }

            if (IsFixtureSensor(fixture) || fixture.CollisionCategories == Category.None)
            {
                return true;
            }

            if (IsOpenWaterCharacterBodyHit(fixture))
            {
                return true;
            }

            if (IsOpenWaterHullVolumeHit(fixture))
            {
                return true;
            }

            if (IsOpenWaterTargetItemHit(fixture, hitWorld))
            {
                return true;
            }

            if (IsExitAirlockStructureHit(fixture, hitWorld))
            {
                return true;
            }

            return passableGapRects.Any(rect => rect.Contains((int)hitWorld.X, (int)hitWorld.Y));
        }

        private bool IsOpenWaterTargetItemHit(Fixture fixture, Vector2 hitWorld)
        {
            if (currentTargetItem == null || currentTargetItem.Removed)
            {
                return false;
            }

            object fixtureUser = GetFixtureUserData(fixture);
            object bodyUser = GetBodyUserData(fixture?.Body);
            if (ReferenceEquals(fixtureUser, currentTargetItem) ||
                ReferenceEquals(bodyUser, currentTargetItem))
            {
                return true;
            }

            if (fixtureUser is Item fixtureItem && IsOpenWaterTargetRelatedItem(fixtureItem))
            {
                return true;
            }

            if (bodyUser is Item bodyItem && IsOpenWaterTargetRelatedItem(bodyItem))
            {
                return true;
            }

            return Vector2.DistanceSquared(hitWorld, currentTargetItem.WorldPosition) <= OpenWaterTargetItemClearance * OpenWaterTargetItemClearance;
        }

        private bool IsOpenWaterTargetRelatedItem(Item item)
        {
            return item == currentTargetItem ||
                   item?.ParentInventory == currentTargetItem.OwnInventory ||
                   currentTargetItem.ParentInventory == item?.OwnInventory;
        }

        private bool IsOpenWaterHullVolumeHit(Fixture fixture)
        {
            object fixtureUser = GetFixtureUserData(fixture);
            object bodyUser = GetBodyUserData(fixture?.Body);
            return IsHullVolumeUserData(fixtureUser) ||
                   IsHullVolumeUserData(bodyUser);
        }

        private static bool IsHullVolumeUserData(object userData)
        {
            if (userData == null)
            {
                return false;
            }

            if (userData is Hull)
            {
                return true;
            }

            string typeName = userData.GetType().FullName ?? userData.GetType().Name;
            return typeName.Equals("Barotrauma.Hull", StringComparison.Ordinal) ||
                   typeName.EndsWith(".Hull", StringComparison.Ordinal);
        }

        private bool IsOpenWaterCharacterBodyHit(Fixture fixture)
        {
            object fixtureUser = GetFixtureUserData(fixture);
            object bodyUser = GetBodyUserData(fixture?.Body);
            return IsCharacterBodyUserData(fixtureUser) ||
                   IsCharacterBodyUserData(bodyUser);
        }

        private bool IsCharacterBodyUserData(object userData)
        {
            if (userData == null)
            {
                return false;
            }

            if (userData is Character)
            {
                return true;
            }

            string typeName = userData.GetType().FullName ?? userData.GetType().Name;
            if (typeName.IndexOf("Limb", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            object owner =
                GetMemberValue(userData, "Character") ??
                GetMemberValue(userData, "character") ??
                GetMemberValue(userData, "Owner") ??
                GetMemberValue(userData, "owner");

            return owner is Character;
        }

        private bool IsExitAirlockStructureHit(Fixture fixture, Vector2 hitWorld)
        {
            if (exitAirlockHull == null || exitAirlockGap == null)
            {
                return false;
            }

            float waterRatio = GetHullWaterRatio(exitAirlockHull);
            Door exitDoor = exitAirlockGap.ConnectedDoor;
            if (waterRatio < AirlockFloodThreshold || exitDoor == null || !exitDoor.IsOpen)
            {
                return false;
            }

            object fixtureUser = GetFixtureUserData(fixture);
            object bodyUser = GetBodyUserData(fixture?.Body);
            if (fixtureUser is not Structure && bodyUser is not Structure)
            {
                return false;
            }

            Vector2 gapCenter = GetGapCenter(exitAirlockGap);
            Rectangle airlockRect = GetWorldRect(exitAirlockHull);
            float airlockRadius = Math.Max(airlockRect.Width, airlockRect.Height) * 0.5f + 50.0f;
            float distToAirlock = Vector2.DistanceSquared(hitWorld, gapCenter);
            return distToAirlock <= airlockRadius * airlockRadius;
        }

        private void LogOpenWaterRaycastHit(Fixture fixture, Vector2 hitWorld, Vector2 startWorld, Vector2 endWorld, Vector2 normal, float fraction)
        {
            if (openWaterObstacleLogTimer > 0.0f)
            {
                return;
            }

            openWaterObstacleLogTimer = 1.0f;
            LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water raycast blocked for {character.Name}: hit=({hitWorld.X:0},{hitWorld.Y:0}), start=({startWorld.X:0},{startWorld.Y:0}), end=({endWorld.X:0},{endWorld.Y:0}), fraction={fraction:0.00}, normal=({normal.X:0.00},{normal.Y:0.00}), fixture={DescribeObject(fixture)}, body={DescribeObject(fixture?.Body)}, fixtureUser={DescribeObject(GetFixtureUserData(fixture))}, bodyUser={DescribeObject(GetBodyUserData(fixture?.Body))}, categories={fixture?.CollisionCategories.ToString() ?? "<null>"}, collidesWith={fixture?.CollidesWith.ToString() ?? "<null>"}, sensor={IsFixtureSensor(fixture)}");
        }

        private static object GetFixtureUserData(Fixture fixture)
        {
            return GetMemberValue(fixture, "UserData") ??
                   GetMemberValue(fixture, "userData");
        }

        private static object GetBodyUserData(Body body)
        {
            return GetMemberValue(body, "UserData") ??
                   GetMemberValue(body, "userData");
        }

        private static object GetMemberValue(object target, string name)
        {
            if (target == null)
            {
                return null;
            }

            try
            {
                return AccessTools.Property(target.GetType(), name)?.GetValue(target) ??
                       AccessTools.Field(target.GetType(), name)?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static string DescribeObject(object value)
        {
            if (value == null)
            {
                return "<null>";
            }

            string typeName = value.GetType().FullName ?? value.GetType().Name;
            switch (value)
            {
                case Item item:
                    return $"{typeName}:{item.Name}";
                case Structure _:
                    return typeName;
                case Hull hull:
                    return $"{typeName}:{GetHullName(hull)}";
                case Door door:
                    return $"{typeName}:doorOpen={door.IsOpen}";
                case Character hitCharacter:
                    return $"{typeName}:{hitCharacter.Name}";
                case Submarine submarine:
                    return $"{typeName}:{submarine.Info?.Name}";
                default:
                    return typeName;
            }
        }

        private static bool IsFixtureSensor(Fixture fixture)
        {
            try
            {
                object sensor =
                    AccessTools.Property(fixture.GetType(), "IsSensor")?.GetValue(fixture) ??
                    AccessTools.Field(fixture.GetType(), "IsSensor")?.GetValue(fixture) ??
                    AccessTools.Field(fixture.GetType(), "_isSensor")?.GetValue(fixture);
                return sensor is bool value && value;
            }
            catch
            {
                return false;
            }
        }

        private List<Rectangle> GetOpenWaterPassableGapRects()
        {
            if (openWaterCachedGapRects != null)
            {
                return openWaterCachedGapRects;
            }

            openWaterCachedGapRects = Gap.GapList
                .Where(IsOpenWaterPassableGap)
                .Select(GetInflatedGapWorldRect)
                .Where(rect => rect.Width > 0 && rect.Height > 0)
                .ToList();
            return openWaterCachedGapRects;
        }

        private bool IsOpenWaterPassableGap(Gap gap)
        {
            if (gap == null)
            {
                return false;
            }

            if (gap == exitAirlockGap)
            {
                return true;
            }

            Door door = gap.ConnectedDoor;
            if (door != null)
            {
                return door.OpenState >= 0.5f;
            }

            int minWidth = (int)(OpenWaterCharacterHalfWidth * 2.0f);
            return gap.Rect.Width >= minWidth || gap.Rect.Height >= minWidth;
        }

        private Rectangle GetInflatedGapWorldRect(Gap gap)
        {
            Rectangle rect = gap?.Rect ?? Rectangle.Empty;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return Rectangle.Empty;
            }

            int inflate = (int)Math.Max(OpenWaterNodeClearance, OpenWaterObstacleInflation);

            Vector2 worldPos = gap.WorldPosition;
            return new Rectangle(
                (int)(worldPos.X - rect.Width * 0.5f) - inflate,
                (int)(worldPos.Y - rect.Height * 0.5f) - inflate,
                rect.Width + inflate * 2,
                rect.Height + inflate * 2);
        }

        private Point WorldToOpenWaterNode(Vector2 worldPosition)
        {
            return WorldToOpenWaterNode(worldPosition, OpenWaterGridSize);
        }

        private Vector2 OpenWaterNodeToWorld(Point node)
        {
            return OpenWaterNodeToWorld(node, OpenWaterGridSize);
        }

        private static Point WorldToOpenWaterNode(Vector2 worldPosition, float gridSize)
        {
            return new Point(
                (int)Math.Round(worldPosition.X / gridSize),
                (int)Math.Round(worldPosition.Y / gridSize));
        }

        private static Vector2 OpenWaterNodeToWorld(Point node, float gridSize)
        {
            return new Vector2(node.X * gridSize, node.Y * gridSize);
        }

        private Rectangle GetInflatedHullWorldRect(Hull hull)
        {
            return GetInflatedHullWorldRect(hull, OpenWaterObstacleInflation);
        }

        private Rectangle GetInflatedHullWorldRect(Hull hull, float inflation)
        {
            Rectangle rect = GetWorldRect(hull);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return Rectangle.Empty;
            }

            int inflate = (int)inflation;
            rect.Inflate(inflate, inflate);
            return rect;
        }

        private static Rectangle GetWorldRect(object entity)
        {
            if (entity is Hull hull)
            {
                Vector2 size = hull.Size;
                if (size.X <= 0 || size.Y <= 0)
                {
                    return Rectangle.Empty;
                }

                Vector2 worldPos = hull.WorldPosition;
                return new Rectangle(
                    (int)(worldPos.X - size.X * 0.5f),
                    (int)(worldPos.Y - size.Y * 0.5f),
                    (int)size.X,
                    (int)size.Y);
            }

            object rectObject =
                AccessTools.Property(entity.GetType(), "WorldRect")?.GetValue(entity) ??
                AccessTools.Field(entity.GetType(), "WorldRect")?.GetValue(entity) ??
                AccessTools.Field(entity.GetType(), "worldRect")?.GetValue(entity);

            return rectObject is Rectangle rect2 ? rect2 : Rectangle.Empty;
        }

        private bool ShouldUseOpenWaterFallback()
        {
            return currentTargetItem != null &&
                   CanUseOpenWaterFallback() &&
                   IsInOpenWaterControlZone();
        }

        private bool CanUseOpenWaterFallback()
        {
            return currentTargetItem != null &&
                   !currentTargetItem.Removed;
        }

        private struct OpenWaterMinHeap
        {
            private readonly List<Point> nodes;
            private readonly Dictionary<Point, float> priorities;

            public int Count => nodes.Count;

            public OpenWaterMinHeap(int capacity)
            {
                nodes = new List<Point>(capacity);
                priorities = new Dictionary<Point, float>(capacity);
            }

            public void Enqueue(Point node, float priority)
            {
                priorities[node] = priority;
                nodes.Add(node);
                SiftUp(nodes.Count - 1);
            }

            public Point Dequeue()
            {
                Point result = nodes[0];
                priorities.Remove(result);
                int last = nodes.Count - 1;
                nodes[0] = nodes[last];
                nodes.RemoveAt(last);
                if (nodes.Count > 0)
                {
                    SiftDown(0);
                }

                return result;
            }

            public bool Contains(Point node)
            {
                return priorities.ContainsKey(node);
            }

            public void UpdatePriority(Point node, float newPriority)
            {
                if (priorities.ContainsKey(node))
                {
                    priorities[node] = newPriority;
                }
            }

            private void SiftUp(int index)
            {
                while (index > 0)
                {
                    int parent = (index - 1) / 2;
                    if (priorities[nodes[index]] >= priorities[nodes[parent]])
                    {
                        break;
                    }

                    Swap(index, parent);
                    index = parent;
                }
            }

            private void SiftDown(int index)
            {
                int count = nodes.Count;
                while (true)
                {
                    int left = 2 * index + 1;
                    int right = 2 * index + 2;
                    int smallest = index;

                    if (left < count && priorities[nodes[left]] < priorities[nodes[smallest]])
                    {
                        smallest = left;
                    }

                    if (right < count && priorities[nodes[right]] < priorities[nodes[smallest]])
                    {
                        smallest = right;
                    }

                    if (smallest == index)
                    {
                        break;
                    }

                    Swap(index, smallest);
                    index = smallest;
                }
            }

            private void Swap(int a, int b)
            {
                (nodes[a], nodes[b]) = (nodes[b], nodes[a]);
            }
        }
    }
}
