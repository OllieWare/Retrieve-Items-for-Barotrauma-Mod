using System;
using Barotrauma;
using HarmonyLib;

namespace RetrieveItemsOrderMod
{
    /// <summary>
    /// Registers the custom order by intercepting AIObjectiveManager.CreateObjective.
    /// This keeps the XML order prefab compatible with the vanilla command UI and
    /// limits the mod's behavior change to the single custom identifier.
    /// </summary>
    [HarmonyPatch(typeof(AIObjectiveManager), nameof(AIObjectiveManager.CreateObjective), new[] { typeof(Order), typeof(float) })]
    internal static class CreateObjectivePatch
    {
        public static bool Prefix(AIObjectiveManager __instance, Order order, float priorityModifier, ref AIObjective __result)
        {
            if (order == null || !RetrieveItemsOrderRules.IsRetrievalOrder(order.Identifier))
            {
                return true;
            }

            Character character = __instance.HumanAIController?.Character;
            if (character == null)
            {
                __result = null;
                return false;
            }

            try
            {
                RetrieveItemsOrderRules.EnsureOrderDisplayData(order);
                // LuaCsLogger.Log($"[RetrieveItemsOrder] Creating objective for {character.Name}");
                if (order.Identifier == RetrieveItemsIds.WreckOrderIdentifier)
                {
                    __result = new AIObjectiveRetrieveWreckItems(character, __instance, order, priorityModifier);
                }
                else
                {
                    __result = new AIObjectiveRetrieveItems(character, __instance, order, priorityModifier);
                }
                __result.Identifier = order.Identifier;
                __result.IgnoreAtOutpost = order.IgnoreAtOutpost;
                // LuaCsLogger.Log($"[RetrieveItemsOrder] Objective created: type={__result.GetType().Name}, identifier={__result.Identifier}, option={order.Option}");
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to create objective for {character.Name}: {ex}");
                __result = null;
            }

            // Vanilla SetOrder only auto-dismisses on abandon for generic objectives.
            // We dismiss explicitly on completion so the order leaves the queue cleanly.
            if (__result == null)
            {
                return false;
            }

            __result.Completed += () =>
            {
                try
                {
                    // LuaCsLogger.Log($"[RetrieveItemsOrder] Objective completed for {character.Name}");
                    AccessTools.Method(typeof(AIObjectiveManager), "DismissSelf")?.Invoke(__instance, new object[] { order });
                }
                catch (Exception ex)
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to dismiss completed order: {ex}");
                }
            };

            return false;
        }
    }
}
