using System;
using Barotrauma;
using HarmonyLib;

namespace RetrieveItemsOrderMod
{
    [HarmonyPatch(typeof(AIObjectiveManager), nameof(AIObjectiveManager.SetOrder), new[] { typeof(Order), typeof(bool) })]
    internal static class SetOrderSpeechPatch
    {
        public static bool Prefix(AIObjectiveManager __instance, Order order, bool speak)
        {
            if (order != null && order.Identifier == RetrieveItemsIds.MarkContainerOrderIdentifier)
            {
                if (!RetrieveItemsOrderRules.IsServerSideRuntime())
                {
                    Item visualTargetItem = RetrieveItemsOrderRules.GetOrderTargetItem(order);
                    // LuaCsLogger.Log($"[RetrieveItemsOrder] Forwarding mark container order to server. target={visualTargetItem?.Name ?? "<null>"}");
                    return true;
                }

                // LuaCsLogger.Log($"[RetrieveItemsOrder] Mark container order selected. option={order.Option}, target={RetrieveItemsOrderRules.GetOrderTargetItem(order)?.Name ?? "<null>"}");
                Character character = __instance.HumanAIController?.Character;
                Item targetItem = RetrieveItemsOrderRules.GetOrderTargetItem(order);
                if (!RetrieveItemsOrderRules.IsMarkableRetrievalTarget(targetItem))
                {
                    LuaCsLogger.Log("[RetrieveItemsOrder] Mark container order aborted: target is not markable retrieval target");
                    return false;
                }

                if (!RetrieveItemsOrderRules.CanMarkContainer(targetItem, character))
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] Mark container order refused for {targetItem.Name}");
                    if (speak && character != null && character.IsOnPlayerTeam)
                    {
                        character.Speak(
                            RetrieveItemsOrderRules.GetText(RetrieveItemsIds.CannotMarkDialog, "Cannot Mark Container"),
                            identifier: RetrieveItemsIds.CannotMarkDialog,
                            minDurationBetweenSimilar: 1.0f);
                    }

                    return false;
                }

                bool marked = RetrieveItemsOrderRules.ToggleMarkedContainer(targetItem);
                // LuaCsLogger.Log($"[RetrieveItemsOrder] {(marked ? "Marked" : "Unmarked")} container: {targetItem.Name}");
                if (speak && character != null && character.IsOnPlayerTeam)
                {
                    Identifier dialogIdentifier = marked ? RetrieveItemsIds.MarkedDialog : RetrieveItemsIds.UnmarkedDialog;
                    character.Speak(
                        RetrieveItemsOrderRules.GetText(dialogIdentifier, marked ? "Container marked for retrieval." : "Container unmarked for retrieval."),
                        identifier: dialogIdentifier,
                        minDurationBetweenSimilar: 1.0f);
                }

                return false;
            }

            if (order == null || order.IsDismissal || !RetrieveItemsOrderRules.IsRetrievalOrder(order.Identifier))
            {
                return true;
            }

            RetrieveItemsOrderRules.EnsureOrderDisplayData(order);
            return true;
        }

        public static void Postfix(AIObjectiveManager __instance, Order order, bool speak)
        {
            if (!speak || order == null || !order.IsDismissal)
            {
                return;
            }

            // LuaCsLogger.Log($"[RetrieveItemsOrder] Dismissal requested for {__instance.HumanAIController?.Character?.Name}, dismissal option={order.Option}, identifier={order.Identifier}");
            if (!TargetsRetrieveItemsOrder(order.Option))
            {
                return;
            }

            Character character = __instance.HumanAIController?.Character;
            if (character == null || !character.IsOnPlayerTeam)
            {
                return;
            }

            // LuaCsLogger.Log($"[RetrieveItemsOrder] Cancelled order for {character.Name}");
            character.Speak(
                RetrieveItemsOrderRules.GetText(RetrieveItemsIds.CancelDialog, "Understood, cancelling retrieval."),
                identifier: RetrieveItemsIds.CancelDialog,
                minDurationBetweenSimilar: 1.0f);
        }

        internal static bool TargetsRetrieveItemsOrder(Identifier dismissOption)
        {
            if (dismissOption == Identifier.Empty)
            {
                return true;
            }

            string identifier = dismissOption.Value.Split('.')[0];
            return string.Equals(identifier, RetrieveItemsIds.OrderIdentifier.Value, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(identifier, RetrieveItemsIds.WreckOrderIdentifier.Value, StringComparison.OrdinalIgnoreCase);
        }
    }
}
