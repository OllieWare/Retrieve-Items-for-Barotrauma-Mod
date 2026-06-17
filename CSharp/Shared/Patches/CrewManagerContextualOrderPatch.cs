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
    internal static class CrewManagerContextualOrderPatch
    {
        public static void Postfix(CrewManager __instance)
        {
            try
            {
                RetrieveItemsPlugin.EnsureHudPatchFromClientContext();
                Item itemContext = AccessTools.Field(typeof(CrewManager), "itemContext")?.GetValue(__instance) as Item;
                if (!RetrieveItemsOrderRules.IsMarkableRetrievalTarget(itemContext))
                {
                    return;
                }

                List<Order> contextualOrders = AccessTools.Field(typeof(CrewManager), "contextualOrders")?.GetValue(__instance) as List<Order>;
                if (contextualOrders == null)
                {
                    // LuaCsLogger.Log("[RetrieveItemsOrder] Contextual order list is null");
                    return;
                }

                if (contextualOrders.Any(o => o?.Identifier == RetrieveItemsIds.MarkContainerOrderIdentifier))
                {
                    return;
                }

                OrderPrefab prefab = RetrieveItemsOrderRules.GetOrderPrefab(RetrieveItemsIds.MarkContainerOrderIdentifier);
                if (prefab == null)
                {
                    LuaCsLogger.Log("[RetrieveItemsOrder] Contextual mark prefab missing");
                    return;
                }

                Character orderGiver = Character.Controlled;
                Order order = prefab.CreateInstance(prefab.TargetType, orderGiver, isAutonomous: false)
                    .WithOrderGiver(orderGiver)
                    .WithTargetEntity(itemContext);

                if (prefab.TryGetTargetItemComponent(itemContext, out ItemComponent itemComponent))
                {
                    order = order.WithItemComponent(itemContext, itemComponent);
                }

                contextualOrders.Add(order);
                object centerNode = AccessTools.Field(typeof(CrewManager), "centerNode")?.GetValue(__instance);
                object rectTransform = centerNode == null
                    ? null
                    : AccessTools.Property(centerNode.GetType(), "RectTransform")?.GetValue(centerNode);
                Point nodeSize = AccessTools.Field(typeof(CrewManager), "nodeSize")?.GetValue(__instance) is Point point ? point : new Point(96, 96);
                int nodeDistance = AccessTools.Field(typeof(CrewManager), "nodeDistance")?.GetValue(__instance) is int distance ? distance : Math.Max(nodeSize.X, nodeSize.Y) + 24;
                Point offset = new Point(0, nodeDistance);

                object optionButton = AccessTools.Method(typeof(CrewManager), "CreateOrderOptionNode")?.Invoke(
                    __instance,
                    new object[] { nodeSize, rectTransform, offset, order, -1 });
                object createdButton = AccessTools.Method(typeof(CrewManager), "CreateOrderNode")?.Invoke(
                    __instance,
                    new object[] { nodeSize, rectTransform, offset, order, -1, false, true });

                if (createdButton != null)
                {
                    CopyOptionButtonBehavior(optionButton, createdButton);
                    RemoveNodeRegistration(__instance, optionButton);
                    ConfigureVisibleContextualButton(__instance, createdButton, order, itemContext);
                    if (AccessTools.Field(typeof(CrewManager), "extraOptionNodes")?.GetValue(__instance) is IList extraOptionNodes)
                    {
                        extraOptionNodes.Add(createdButton);
                    }
                }

                // LuaCsLogger.Log($"[RetrieveItemsOrder] Appended contextual mark order for container: {itemContext.Name}, count={contextualOrders.Count}");
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to seed contextual container order: {ex}");
            }
        }

        private static void CopyOptionButtonBehavior(object sourceButton, object targetButton)
        {
            if (sourceButton == null || targetButton == null)
            {
                return;
            }

            CopyMemberValue(sourceButton, targetButton, "OnClicked");
            CopyMemberValue(sourceButton, targetButton, "OnPressed");
            CopyMemberValue(sourceButton, targetButton, "UserData");
            CopyMemberValue(sourceButton, targetButton, "ToolTip");
            CopyMemberValue(sourceButton, targetButton, "Enabled");
            CopyMemberValue(sourceButton, targetButton, "Visible");
        }

        private static void CopyMemberValue(object source, object target, string memberName)
        {
            try
            {
                var property = AccessTools.Property(source.GetType(), memberName);
                if (property != null && property.CanRead)
                {
                    object value = property.GetValue(source);
                    var targetProperty = AccessTools.Property(target.GetType(), memberName);
                    if (targetProperty != null && targetProperty.CanWrite)
                    {
                        targetProperty.SetValue(target, value);
                        return;
                    }

                    var targetField = AccessTools.Field(target.GetType(), memberName);
                    if (targetField != null)
                    {
                        targetField.SetValue(target, value);
                    }
                    return;
                }

                var field = AccessTools.Field(source.GetType(), memberName);
                if (field != null)
                {
                    object value = field.GetValue(source);
                    var targetProperty = AccessTools.Property(target.GetType(), memberName);
                    if (targetProperty != null && targetProperty.CanWrite)
                    {
                        targetProperty.SetValue(target, value);
                        return;
                    }

                    var targetField = AccessTools.Field(target.GetType(), memberName);
                    if (targetField != null)
                    {
                        targetField.SetValue(target, value);
                    }
                }
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to copy button member {memberName}: {ex.Message}");
            }
        }

        private static void RemoveNodeRegistration(CrewManager crewManager, object button)
        {
            if (crewManager == null || button == null)
            {
                return;
            }

            try
            {
                if (AccessTools.Field(typeof(CrewManager), "OrderOptionButtons")?.GetValue(crewManager) is IList orderOptionButtons)
                {
                    orderOptionButtons.Remove(button);
                }

                if (AccessTools.Field(typeof(CrewManager), "optionNodes")?.GetValue(crewManager) is IList optionNodes)
                {
                    for (int i = optionNodes.Count - 1; i >= 0; i--)
                    {
                        object optionNode = optionNodes[i];
                        object nodeButton =
                            AccessTools.Property(optionNode?.GetType(), "Button")?.GetValue(optionNode) ??
                            AccessTools.Field(optionNode?.GetType(), "Button")?.GetValue(optionNode) ??
                            AccessTools.Field(optionNode?.GetType(), "button")?.GetValue(optionNode);
                        if (ReferenceEquals(nodeButton, button))
                        {
                            optionNodes.RemoveAt(i);
                        }
                    }
                }

                AccessTools.Method(button.GetType(), "Remove")?.Invoke(button, null);
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to remove temporary option node registration: {ex.Message}");
            }
        }

        private static void ConfigureVisibleContextualButton(CrewManager crewManager, object button, Order order, Item itemContext)
        {
            if (crewManager == null || button == null || order == null || itemContext == null)
            {
                return;
            }

            try
            {
                AccessTools.Field(button.GetType(), "UserData")?.SetValue(button, order);
                AccessTools.Property(button.GetType(), "UserData")?.SetValue(button, order);

                Type onClickedHandlerType = AccessTools.Field(button.GetType(), "OnClicked")?.FieldType;
                if (onClickedHandlerType == null)
                {
                    // LuaCsLogger.Log("[RetrieveItemsOrder] Visible contextual button has no OnClicked handler type");
                    return;
                }

                Func<object, object, bool> callback = (_, userData) =>
                {
                    Order clickedOrder = userData as Order ?? order;
                    OrderPrefab prefab = RetrieveItemsOrderRules.GetOrderPrefab(RetrieveItemsIds.MarkContainerOrderIdentifier);
                    Character orderGiver = Character.Controlled;
                    if (prefab != null)
                    {
                        clickedOrder = prefab.CreateInstance(prefab.TargetType, orderGiver, isAutonomous: false)
                            .WithOrderGiver(orderGiver)
                            .WithTargetEntity(itemContext);

                        if (prefab.TryGetTargetItemComponent(itemContext, out ItemComponent itemComponent))
                        {
                            clickedOrder = clickedOrder.WithItemComponent(itemContext, itemComponent);
                        }
                    }

                    // LuaCsLogger.Log($"[RetrieveItemsOrder] Visible contextual button clicked for {clickedOrder.Identifier}, target={RetrieveItemsOrderRules.GetOrderTargetItem(clickedOrder)?.Name ?? itemContext.Name}");
                    bool desiredMarkedState = !RetrieveItemsOrderRules.IsMarkedContainer(itemContext);
                    RetrieveItemsOrderRules.SetMarkedContainerState(itemContext, desiredMarkedState);
                    RetrieveItemsOrderRules.SendMarkContainerRelay(itemContext, desiredMarkedState);
                    if (orderGiver != null && orderGiver.IsOnPlayerTeam)
                    {
                        Identifier dialogIdentifier = desiredMarkedState ? RetrieveItemsIds.MarkedDialog : RetrieveItemsIds.UnmarkedDialog;
                        orderGiver.Speak(
                            RetrieveItemsOrderRules.GetText(dialogIdentifier, desiredMarkedState ? "Container marked for retrieval." : "Container unmarked for retrieval."),
                            identifier: dialogIdentifier,
                            minDurationBetweenSimilar: 1.0f);
                    }

                    bool added = false;
                    try
                    {
                        object result = AccessTools.Method(typeof(CrewManager), "AddOrder", new[] { typeof(Order), typeof(float?) })
                            ?.Invoke(crewManager, new object[] { clickedOrder, null });
                        added = result is bool success && success;
                    }
                    catch (Exception ex)
                    {
                        LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to add visible contextual order: {ex}");
                    }

                    // LuaCsLogger.Log($"[RetrieveItemsOrder] Visible contextual AddOrder result={added}");
                    AccessTools.Method(typeof(CrewManager), "DisableCommandUI")?.Invoke(crewManager, null);
                    return true;
                };

                var handler = Delegate.CreateDelegate(
                    onClickedHandlerType,
                    callback.Target,
                    callback.Method);
                AccessTools.Field(button.GetType(), "OnClicked")?.SetValue(button, handler);
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to configure visible contextual button: {ex}");
            }
        }
    }
}
