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
        private const float OpenWaterGridSize = 100.0f;
        private const float OpenWaterCloseEnough = 180.0f;
        private const float OpenWaterWaypointCloseEnough = 100.0f;
        private const float OpenWaterRepathInterval = 2.0f;
        private const float OpenWaterObstacleInflation = 40.0f;
        private const float OpenWaterNodeClearance = 40.0f;
        private const int OpenWaterNearestNodeSearchRadius = 20;

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

            float targetDistance = Vector2.Distance(character.WorldPosition, targetWorldPosition);
            if (!IsInOpenWaterControlZone())
            {
                StopOpenWaterFallback();
                return false;
            }

            if (targetDistance <= closeEnough)
            {
                character.OverrideMovement = null;
                return true;
            }

            openWaterRepathTimer -= deltaTime;
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
                List<Rectangle> debugObstacles = GetOpenWaterObstacles();
                LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water path for {character.Name}: target={targetLabel}, nodes={openWaterPath.Count}, distance={targetDistance:0}, obstacles={debugObstacles.Count}, directBlocked={OpenWaterSegmentBlocked(character.WorldPosition, targetWorldPosition, debugObstacles)}, steering=world-override");
                if (openWaterPath.Count == 0)
                {
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
                if (Vector2.DistanceSquared(character.WorldPosition, nextPoint) > OpenWaterWaypointCloseEnough * OpenWaterWaypointCloseEnough)
                {
                    break;
                }

                if (openWaterPathIndex + 1 < openWaterPath.Count)
                {
                    List<Rectangle> obstacles = GetOpenWaterObstacles();
                    Vector2 followingPoint = openWaterPath[openWaterPathIndex + 1];
                    if (OpenWaterSegmentBlocked(character.WorldPosition, followingPoint, obstacles))
                    {
                        break;
                    }
                }

                openWaterPathIndex++;
            }

            if (openWaterPathIndex >= openWaterPath.Count)
            {
                nextPoint = targetWorldPosition;
            }

            float currentTargetDistanceSquared = Vector2.DistanceSquared(character.WorldPosition, targetWorldPosition);
            while (openWaterPathIndex < openWaterPath.Count &&
                   Vector2.DistanceSquared(nextPoint, targetWorldPosition) > currentTargetDistanceSquared + (OpenWaterGridSize * OpenWaterGridSize))
            {
                openWaterPathIndex++;
                nextPoint = openWaterPathIndex < openWaterPath.Count ? openWaterPath[openWaterPathIndex] : targetWorldPosition;
            }

            if (Vector2.DistanceSquared(nextPoint, targetWorldPosition) > currentTargetDistanceSquared + (OpenWaterGridSize * OpenWaterGridSize))
            {
                nextPoint = targetWorldPosition;
            }

            float waypointDistance = Vector2.Distance(character.WorldPosition, nextPoint);
            UpdateOpenWaterProgress(deltaTime, waypointDistance, targetDistance);

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

            bool nearExitAirlock =
                character.CurrentHull == exitAirlockHull ||
                IsCharacterInsideHullBounds(exitAirlockHull) ||
                Vector2.DistanceSquared(character.WorldPosition, GetGapCenter(exitAirlockGap)) < 900.0f * 900.0f;
            if (!nearExitAirlock)
            {
                return false;
            }

            OpenExitAirlockDoor(exitAirlockGap);
            Vector2 exitPoint = GetExternalExitPoint(exitAirlockHull, exitAirlockGap);
            Vector2 movement = exitPoint - character.WorldPosition;
            if (movement.LengthSquared() <= 1.0f)
            {
                return false;
            }

            ApplyOpenWaterSteering(deltaTime, movement, targetDistance, exitPoint, targetWorldPosition);
            return true;
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

            if (travelPhase == WreckTravelPhase.OpenWater &&
                currentHull == exitAirlockHull &&
                character.InWater)
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
                openWaterRepathTimer = 0.0f;
                openWaterPath.Clear();
                openWaterPathIndex = 0;
                LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water navigation made no progress for {character.Name}; forcing repath, distance={targetDistance:0}, waypointDistance={waypointDistance:0}, steering=world-override");
            }
        }

        private List<Vector2> BuildOpenWaterPath(Vector2 start, Vector2 goal)
        {
            List<Rectangle> obstacles = GetOpenWaterObstacles();
            if (!OpenWaterSegmentBlocked(start, goal, obstacles))
            {
                return new List<Vector2> { start, goal };
            }

            Vector2 startAnchor = GetOpenWaterStartAnchor(start);
            Rectangle bounds = GetOpenWaterSearchBounds(startAnchor, goal);
            Point preferredStartNode = WorldToOpenWaterNode(startAnchor);
            Point preferredGoalNode = WorldToOpenWaterNode(goal);
            Point? resolvedStartNode = FindNearestOpenWaterNode(preferredStartNode, startAnchor, obstacles, bounds);
            Point? resolvedGoalNode = FindNearestOpenWaterNode(preferredGoalNode, goal, obstacles, bounds);
            if (resolvedStartNode == null || resolvedGoalNode == null)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water path failed for {character.Name}: resolvedStart={resolvedStartNode.HasValue}, resolvedGoal={resolvedGoalNode.HasValue}, startAnchor=({startAnchor.X:0},{startAnchor.Y:0})");
                return GetFallbackOpenWaterPath(start, goal, obstacles);
            }

            Point startNode = resolvedStartNode.Value;
            Point goalNode = resolvedGoalNode.Value;
            if (startNode == goalNode)
            {
                return OpenWaterSegmentBlocked(start, goal, obstacles)
                    ? GetFallbackOpenWaterPath(start, goal, obstacles)
                    : new List<Vector2> { start, goal };
            }

            Dictionary<Point, Point> cameFrom = new Dictionary<Point, Point>();
            Dictionary<Point, float> costSoFar = new Dictionary<Point, float>();
            List<Point> open = new List<Point> { startNode };
            HashSet<Point> closed = new HashSet<Point>();
            costSoFar[startNode] = 0.0f;

            while (open.Count > 0)
            {
                Point current = open
                    .OrderBy(point => costSoFar[point] + OpenWaterHeuristic(point, goalNode))
                    .First();
                open.Remove(current);

                if (current == goalNode)
                {
                    return ReconstructOpenWaterPath(cameFrom, current);
                }

                closed.Add(current);
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
                        if (!open.Contains(next))
                        {
                            open.Add(next);
                        }
                    }
                }
            }

            LuaCsLogger.Log($"[RetrieveItemsOrder] Open-water path exhausted for {character.Name}: explored={closed.Count}, bounds=({bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}), startNode=({startNode.X},{startNode.Y}), goalNode=({goalNode.X},{goalNode.Y})");
            return GetFallbackOpenWaterPath(start, goal, obstacles);
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
                .Select(GetInflatedHullWorldRect)
                .Where(rect => rect.Width > 0 && rect.Height > 0)
                .ToList();
        }

        private List<Vector2> GetFallbackOpenWaterPath(Vector2 start, Vector2 goal, List<Rectangle> obstacles)
        {
            return OpenWaterSegmentBlocked(start, goal, obstacles)
                ? new List<Vector2>()
                : new List<Vector2> { start, goal };
        }

        private Rectangle GetOpenWaterSearchBounds(Vector2 start, Vector2 goal)
        {
            int margin = (int)Math.Max(OpenWaterGridSize * 12.0f, Vector2.Distance(start, goal) * 1.25f);
            int minX = (int)Math.Floor(Math.Min(start.X, goal.X) - margin);
            int minY = (int)Math.Floor(Math.Min(start.Y, goal.Y) - margin);
            int maxX = (int)Math.Ceiling(Math.Max(start.X, goal.X) + margin);
            int maxY = (int)Math.Ceiling(Math.Max(start.Y, goal.Y) + margin);
            Point min = WorldToOpenWaterNode(new Vector2(minX, minY));
            Point max = WorldToOpenWaterNode(new Vector2(maxX, maxY));
            return new Rectangle(min.X, min.Y, Math.Max(max.X - min.X, 1), Math.Max(max.Y - min.Y, 1));
        }

        private Point? FindNearestOpenWaterNode(Point preferredNode, Vector2 preferredWorldPosition, List<Rectangle> obstacles, Rectangle bounds)
        {
            if (bounds.Contains(preferredNode.X, preferredNode.Y) && !OpenWaterNodeBlocked(preferredNode, obstacles))
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
                        Vector2 candidateWorld = OpenWaterNodeToWorld(candidate);
                        if (!bounds.Contains(candidate.X, candidate.Y) ||
                            OpenWaterNodeBlocked(candidate, obstacles))
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
            List<Vector2> path = new List<Vector2> { OpenWaterNodeToWorld(current) };
            while (cameFrom.TryGetValue(current, out Point previous))
            {
                current = previous;
                path.Add(OpenWaterNodeToWorld(current));
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
                int next = path.Count - 1;
                while (next > anchor + 1 && OpenWaterSegmentBlocked(path[anchor], path[next], obstacles))
                {
                    next--;
                }

                smoothed.Add(path[next]);
                anchor = next;
            }

            return smoothed;
        }

        private bool OpenWaterNodeBlocked(Point node, List<Rectangle> obstacles)
        {
            Vector2 world = OpenWaterNodeToWorld(node);
            if (OpenWaterRectangleObstacleBlocked(world, obstacles, GetOpenWaterPassableGapRects()))
            {
                return true;
            }

            return OpenWaterPhysicsPointBlocked(world);
        }

        private bool OpenWaterSegmentBlocked(Vector2 start, Vector2 end, List<Rectangle> obstacles)
        {
            if (OpenWaterPhysicsSegmentBlocked(start, end))
            {
                return true;
            }

            float distance = Vector2.Distance(start, end);
            int steps = Math.Max((int)(distance / (OpenWaterGridSize * 0.5f)), 1);
            List<Rectangle> passableGapRects = GetOpenWaterPassableGapRects();
            for (int i = 0; i <= steps; i++)
            {
                Vector2 point = Vector2.Lerp(start, end, i / (float)steps);
                if (OpenWaterRectangleObstacleBlocked(point, obstacles, passableGapRects))
                {
                    return true;
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
            Vector2 horizontalStart = world + new Vector2(-OpenWaterNodeClearance, 0.0f);
            Vector2 horizontalEnd = world + new Vector2(OpenWaterNodeClearance, 0.0f);
            if (OpenWaterPhysicsSegmentBlocked(horizontalStart, horizontalEnd))
            {
                return true;
            }

            Vector2 verticalStart = world + new Vector2(0.0f, -OpenWaterNodeClearance);
            Vector2 verticalEnd = world + new Vector2(0.0f, OpenWaterNodeClearance);
            return OpenWaterPhysicsSegmentBlocked(verticalStart, verticalEnd);
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

            if (Vector2.DistanceSquared(hitWorld, startWorld) < OpenWaterNodeClearance * OpenWaterNodeClearance)
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

            return Vector2.DistanceSquared(hitWorld, currentTargetItem.WorldPosition) <= OpenWaterCloseEnough * OpenWaterCloseEnough;
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
            return Gap.GapList
                .Where(IsOpenWaterPassableGap)
                .Select(GetInflatedGapWorldRect)
                .Where(rect => rect.Width > 0 && rect.Height > 0)
                .ToList();
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
            return door == null || door.IsOpen;
        }

        private Rectangle GetInflatedGapWorldRect(Gap gap)
        {
            Rectangle rect = gap?.Rect ?? Rectangle.Empty;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return Rectangle.Empty;
            }

            int inflate = (int)Math.Max(OpenWaterNodeClearance, OpenWaterObstacleInflation);
            rect.Inflate(inflate, inflate);
            return rect;
        }

        private Point WorldToOpenWaterNode(Vector2 worldPosition)
        {
            return new Point(
                (int)Math.Round(worldPosition.X / OpenWaterGridSize),
                (int)Math.Round(worldPosition.Y / OpenWaterGridSize));
        }

        private Vector2 OpenWaterNodeToWorld(Point node)
        {
            return new Vector2(node.X * OpenWaterGridSize, node.Y * OpenWaterGridSize);
        }

        private Rectangle GetInflatedHullWorldRect(Hull hull)
        {
            Rectangle rect = GetWorldRect(hull);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return Rectangle.Empty;
            }

            int inflate = (int)OpenWaterObstacleInflation;
            rect.Inflate(inflate, inflate);
            return rect;
        }

        private static Rectangle GetWorldRect(object entity)
        {
            object rectObject =
                AccessTools.Property(entity.GetType(), "WorldRect")?.GetValue(entity) ??
                AccessTools.Field(entity.GetType(), "WorldRect")?.GetValue(entity) ??
                AccessTools.Field(entity.GetType(), "worldRect")?.GetValue(entity);

            return rectObject is Rectangle rect ? rect : Rectangle.Empty;
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
    }
}
