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
    public static class RetrieveItemsOrderRules
    {
        private const string MarkRelayPrefix = "__retrieveitems_mark__";
        private static readonly Dictionary<Item, int> markVersions = new Dictionary<Item, int>();

        public static void EnsureOrderDisplayData(Order order)
        {
            if (order == null || !IsRetrievalOrder(order.Identifier))
            {
                return;
            }

            try
            {
                if (order.Option == Identifier.Empty)
                {
                    AccessTools.Property(typeof(Order), "Option")?.SetValue(order, order.Identifier, null);
                    AccessTools.Field(typeof(Order), "option")?.SetValue(order, order.Identifier);
                }
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to normalize order option: {ex}");
            }
        }

        public static bool IsRetrievalOrder(Identifier identifier)
        {
            return identifier == RetrieveItemsIds.OrderIdentifier ||
                   identifier == RetrieveItemsIds.WreckOrderIdentifier;
        }

        public static bool IsMarkableRetrievalTarget(Item item)
        {
            return item != null &&
                   !item.Removed &&
                   (item.GetComponent<ItemContainer>() != null || IsPortableRetrievalTarget(item));
        }

        public static bool IsPortableRetrievalTarget(Item item)
        {
            if (item == null || item.Removed)
            {
                return false;
            }

            if (item.GetComponent<Holdable>() == null)
            {
                return false;
            }

            string identifier = item.Prefab?.Identifier.Value ?? string.Empty;
            string name = item.Name.ToString() ?? string.Empty;
            return item.HasTag("crate".ToIdentifier()) ||
                   item.HasTag("ammobox".ToIdentifier()) ||
                   item.HasTag("mobilecontainer".ToIdentifier()) ||
                   item.HasTag("artifactcontainer".ToIdentifier()) ||
                   identifier.IndexOf("crate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identifier.IndexOf("box", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identifier.IndexOf("case", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identifier.IndexOf("container", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("crate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("box", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("case", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("container", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string GetText(Identifier identifier, string fallback)
        {
            try
            {
                string text = TextManager.Get(identifier).Value;
                if (!string.IsNullOrWhiteSpace(text) && text != identifier.Value)
                {
                    return text;
                }
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to resolve text {identifier}: {ex.Message}");
            }

            return fallback;
        }

        public static bool ToggleMarkedContainer(Item container)
        {
            if (container == null)
            {
                return false;
            }

            if (IsMarkedContainer(container))
            {
                SetMarkedContainerState(container, false);
                return false;
            }

            SetMarkedContainerState(container, true);
            return true;
        }

        public static void SendMarkContainerRelay(Item container, bool marked)
        {
            if (container == null)
            {
                return;
            }

            if (IsServerSideRuntime())
            {
                SetMarkedContainerState(container, marked);
                return;
            }

            try
            {
                string relayText = $"{MarkRelayPrefix}:{container.ID}:{(marked ? "1" : "0")}";
                if (TrySendHiddenMarkRelay(relayText))
                {
                    // LuaCsLogger.Log($"[RetrieveItemsOrder] Sent server mark relay for {container.Name}: marked={marked}, id={container.ID}");
                }
                else
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to send server mark relay for {container.Name}: client send API unavailable");
                }
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to send server mark relay for {container.Name}: {ex}");
            }
        }

        public static void BroadcastMarkContainerRelay(Item container, bool marked)
        {
            if (container == null || !IsServerSideRuntime())
            {
                return;
            }

            try
            {
                string relayText = $"{MarkRelayPrefix}:{container.ID}:{(marked ? "1" : "0")}";
                object server =
                    AccessTools.Property(typeof(GameMain), "Server")?.GetValue(null) ??
                    AccessTools.Field(typeof(GameMain), "Server")?.GetValue(null) ??
                    AccessTools.Field(typeof(GameMain), "server")?.GetValue(null);
                object connectedClients = AccessTools.Property(server?.GetType(), "ConnectedClients")?.GetValue(server);
                if (server == null || connectedClients is not IEnumerable clients)
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to broadcast mark relay for {container.Name}: server clients unavailable");
                    return;
                }

                Type chatMessageType = AccessTools.TypeByName("Barotrauma.Networking.ChatMessageType");
                if (chatMessageType == null)
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to broadcast mark relay for {container.Name}: chat type unavailable");
                    return;
                }

                object defaultChatType = Enum.Parse(chatMessageType, "Default");
                var sendMethod = server
                    .GetType()
                    .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "SendDirectChatMessage") { return false; }
                        var p = m.GetParameters();
                        return p.Length == 3 &&
                               p[0].ParameterType == typeof(string) &&
                               p[2].ParameterType.FullName == "Barotrauma.Networking.ChatMessageType";
                    });
                if (sendMethod == null)
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to broadcast mark relay for {container.Name}: direct chat API unavailable");
                    return;
                }

                int sent = 0;
                foreach (object client in clients)
                {
                    sendMethod.Invoke(server, new[] { relayText, client, defaultChatType });
                    sent++;
                }
                // LuaCsLogger.Log($"[RetrieveItemsOrder] Broadcast mark relay for {container.Name}: marked={marked}, clients={sent}");
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to broadcast mark relay for {container.Name}: {ex}");
            }
        }

        private static bool TrySendHiddenMarkRelay(string relayText)
        {
            object client =
                AccessTools.Property(typeof(GameMain), "Client")?.GetValue(null) ??
                AccessTools.Field(typeof(GameMain), "Client")?.GetValue(null) ??
                AccessTools.Field(typeof(GameMain), "client")?.GetValue(null);
            if (client == null)
            {
                return false;
            }

            Type chatMessageType = AccessTools.TypeByName("Barotrauma.Networking.ChatMessageType");
            if (chatMessageType == null)
            {
                return false;
            }

            object defaultChatType = Enum.Parse(chatMessageType, "Default");
            var sendMethod = client
                .GetType()
                .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "SendChatMessage") { return false; }
                    var p = m.GetParameters();
                    return p.Length == 2 &&
                           p[0].ParameterType == typeof(string) &&
                           p[1].ParameterType.FullName == "Barotrauma.Networking.ChatMessageType";
                });

            if (sendMethod == null)
            {
                return false;
            }

            sendMethod.Invoke(client, new[] { relayText, defaultChatType });
            return true;
        }

        public static bool TryHandleMarkContainerRelay(object chatMessage)
        {
            try
            {
                string text = GetMemberValue(chatMessage, "Text") as string;
                if (string.IsNullOrWhiteSpace(text) || !text.StartsWith(MarkRelayPrefix + ":", StringComparison.Ordinal))
                {
                    return false;
                }

                string[] parts = text.Split(':');
                if (parts.Length != 3 ||
                    !ushort.TryParse(parts[1], out ushort itemId) ||
                    !int.TryParse(parts[2], out int markedInt))
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] Ignoring malformed mark relay: {text}");
                    return true;
                }

                Item container = Item.ItemList.FirstOrDefault(item =>
                    item != null &&
                    !item.Removed &&
                    item.ID == itemId &&
                    IsMarkableRetrievalTarget(item));
                if (container == null)
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] Mark relay target missing: id={itemId}");
                    return true;
                }

                object senderClient = GetMemberValue(chatMessage, "SenderClient");
                Character relayCharacter =
                    GetMemberValue(senderClient, "Character") as Character ??
                    GetMemberValue(chatMessage, "Sender") as Character;
                bool serverOriginatedClientRelay = !IsServerSideRuntime() && relayCharacter == null;
                if (!serverOriginatedClientRelay && !CanMarkContainer(container, relayCharacter))
                {
                    LuaCsLogger.Log($"[RetrieveItemsOrder] Mark relay refused for {container.Name}");
                    return true;
                }

                bool marked = markedInt != 0;
                SetMarkedContainerState(container, marked);
                // LuaCsLogger.Log($"[RetrieveItemsOrder] Applied mark relay for {container.Name}: marked={marked}, id={itemId}");
                return true;
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to handle mark relay: {ex}");
                return true;
            }
        }

        private static object GetMemberValue(object instance, string memberName)
        {
            if (instance == null)
            {
                return null;
            }

            return AccessTools.Property(instance.GetType(), memberName)?.GetValue(instance) ??
                   AccessTools.Field(instance.GetType(), memberName)?.GetValue(instance);
        }

        public static void SetMarkedContainerState(Item container, bool marked)
        {
            if (container == null)
            {
                return;
            }

            if (!marked)
            {
                RestoreContainerVisual(container);
                return;
            }

            markVersions.TryGetValue(container, out int version);
            markVersions[container] = version + 1;
            container.AddTag(RetrieveItemsIds.MarkedContainerTag);

            if (markVersions.Count > 100)
            {
                foreach (var key in markVersions.Keys.Where(k => k == null || k.Removed).ToList())
                {
                    markVersions.Remove(key);
                }
            }
        }

        public static bool IsServerSideRuntime()
        {
            try
            {
                object server =
                    AccessTools.Property(typeof(GameMain), "Server")?.GetValue(null) ??
                    AccessTools.Field(typeof(GameMain), "Server")?.GetValue(null) ??
                    AccessTools.Field(typeof(GameMain), "server")?.GetValue(null);
                return server != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsMarkedContainer(Item container)
        {
            return container != null && !container.Removed && container.HasTag(RetrieveItemsIds.MarkedContainerTag);
        }

        public static int GetMarkVersion(Item container)
        {
            if (container == null)
            {
                return 0;
            }

            markVersions.TryGetValue(container, out int version);
            return version;
        }

        public static IEnumerable<Item> GetMarkedContainers(Submarine targetOutpost)
        {
            return Item.ItemList.Where(item =>
                item != null &&
                !item.Removed &&
                item.Submarine == targetOutpost &&
                item.HasTag(RetrieveItemsIds.MarkedContainerTag)).ToList();
        }

        public static IEnumerable<Item> GetMarkedContainers()
        {
            return Item.ItemList.Where(item =>
                item != null &&
                !item.Removed &&
                item.HasTag(RetrieveItemsIds.MarkedContainerTag)).ToList();
        }

        public static Item GetOrderTargetItem(Order order)
        {
            if (order == null)
            {
                return null;
            }

            return AccessTools.Property(typeof(Order), "TargetEntity")?.GetValue(order) as Item ??
                   AccessTools.Field(typeof(Order), "targetEntity")?.GetValue(order) as Item;
        }

        public static OrderPrefab GetOrderPrefab(Identifier identifier)
        {
            try
            {
                return OrderPrefab.Prefabs.FirstOrDefault(prefab => prefab?.Identifier == identifier);
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to find order prefab {identifier}: {ex}");
                return null;
            }
        }

        public static void ClearMarkedContainers()
        {
            foreach (Item container in GetMarkedContainers().ToList())
            {
                RestoreContainerVisual(container);
            }
        }

        private static void RestoreContainerVisual(Item container)
        {
            if (container == null || container.Removed)
            {
                return;
            }

            container.RemoveTag(RetrieveItemsIds.MarkedContainerTag);
        }

        public static bool CanAcceptOrder(Character character, out Submarine targetLocation)
        {
            targetLocation = FindTargetLocation(character);
            return targetLocation != null &&
                   !HasHostiles(targetLocation, character);
        }

        public static Submarine FindTargetLocation(Character character)
        {
            Submarine homeSubmarine = ResolveHomeSubmarine(character);
            if (homeSubmarine == null)
            {
                return null;
            }

            IEnumerable<Submarine> dockedSubs = homeSubmarine.DockedTo ?? Enumerable.Empty<Submarine>();

            return dockedSubs
                .Where(s => s != null)
                .Where(s => IsValidRetrievalLocation(s, homeSubmarine))
                .OrderByDescending(NameLooksAbandoned)
                .ThenBy(s => Vector2.DistanceSquared(character.WorldPosition, s.WorldPosition))
                .FirstOrDefault();
        }

        public static Submarine ResolveHomeSubmarine(Character character)
        {
            if (character?.Submarine?.Info?.Type == SubmarineType.Player)
            {
                return character.Submarine;
            }

            if (Submarine.MainSub?.Info?.Type == SubmarineType.Player)
            {
                return Submarine.MainSub;
            }

            return character?.Submarine ?? Submarine.MainSub;
        }

        public static bool HasHostiles(Submarine targetLocation, Character orderedCharacter)
        {
            if (targetLocation == null)
            {
                return true;
            }

            return Character.CharacterList.Any(c =>
                c != null &&
                !c.Removed &&
                !c.IsDead &&
                c.Submarine == targetLocation &&
                c != orderedCharacter &&
                !c.IsOnPlayerTeam);
        }

        public static bool CanMarkContainer(Item container, Character orderedCharacter)
        {
            return IsMarkableRetrievalTarget(container);
        }

        public static bool IsCharacterInsideTarget(Character character, Submarine targetLocation)
        {
            if (character == null || targetLocation == null)
            {
                return false;
            }

            return character.Submarine == targetLocation || character.CurrentHull?.Submarine == targetLocation;
        }

        private static bool IsValidRetrievalLocation(Submarine submarine, Submarine homeSubmarine)
        {
            if (submarine == null || submarine == homeSubmarine || submarine.Info == null)
            {
                return false;
            }

            if (NameLooksAbandoned(submarine))
            {
                return true;
            }

            return submarine.Info.Type switch
            {
                SubmarineType.BeaconStation => true,
                SubmarineType.Outpost => true,
                SubmarineType.OutpostModule => true,
                _ => false
            };
        }

        private static bool NameLooksAbandoned(Submarine submarine)
        {
            string name = submarine.Info?.Name?.ToString() ?? string.Empty;
            return name.IndexOf("abandoned", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
