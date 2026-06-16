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
    /// <summary>
    /// LuaCs in-memory C# plugin entry point.
    /// Barotrauma automatically compiles files inside CSharp/Shared for enabled content packages,
    /// so there is no extra filelist.xml entry for this source file.
    /// </summary>
    public sealed class RetrieveItemsPlugin : IAssemblyPlugin
    {
        private static Harmony harmony;
        private static bool attemptedHudPatch;
        private static bool hudPatchApplied;

        public void Initialize()
        {
            harmony = new Harmony("mod.retrieveitemsorder");
            harmony.PatchAll();
            TryPatchCrewManagerContextualOrders();
            TryPatchMarkedContainerHud();
            TryPatchGameServerChatRelay();
            TryPatchGameClientChatRelay();
            // LuaCsLogger.Log("[RetrieveItemsOrder] Initialize");
        }

        public void PreInitPatching()
        {
        }

        public void OnLoadCompleted()
        {
            // LuaCsLogger.Log("[RetrieveItemsOrder] Load completed");
        }

        public void Dispose()
        {
            RetrieveItemsOrderRules.ClearMarkedContainers();
            harmony?.UnpatchAll("mod.retrieveitemsorder");
            // LuaCsLogger.Log("[RetrieveItemsOrder] Dispose");
        }

        private void TryPatchCrewManagerContextualOrders()
        {
            try
            {
                var targetMethod = AccessTools.Method(typeof(CrewManager), "CreateContextualOrderNodes");
                var postfixMethod = AccessTools.Method(typeof(CrewManagerContextualOrderPatch), nameof(CrewManagerContextualOrderPatch.Postfix));
                if (targetMethod == null || postfixMethod == null)
                {
                    LuaCsLogger.Log("[RetrieveItemsOrder] Skipping CrewManager contextual patch; method not available in this runtime");
                    return;
                }

                harmony.Patch(targetMethod, postfix: new HarmonyMethod(postfixMethod));
                // LuaCsLogger.Log("[RetrieveItemsOrder] Patched CrewManager contextual order UI");
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to patch CrewManager contextual UI: {ex}");
            }
        }

        private void TryPatchMarkedContainerHud()
        {
            TryPatchMarkedContainerHud("init");
        }

        private void TryPatchGameServerChatRelay()
        {
            try
            {
                Type gameServerType = AccessTools.TypeByName("Barotrauma.Networking.GameServer");
                var targetMethod = gameServerType == null
                    ? null
                    : gameServerType
                        .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                        .FirstOrDefault(m =>
                        {
                            if (m.Name != "AddChatMessage") { return false; }
                            var p = m.GetParameters();
                            return p.Length == 1 &&
                                   p[0].ParameterType.FullName == "Barotrauma.Networking.ChatMessage";
                        });
                var prefixMethod = AccessTools.Method(typeof(MarkContainerChatRelayPatch), nameof(MarkContainerChatRelayPatch.Prefix));
                if (targetMethod == null || prefixMethod == null)
                {
                    LuaCsLogger.Log("[RetrieveItemsOrder] Skipping mark relay chat patch; GameServer.AddChatMessage unavailable");
                    return;
                }

                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefixMethod));
                // LuaCsLogger.Log("[RetrieveItemsOrder] Patched server mark relay chat handler");
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to patch mark relay chat handler: {ex}");
            }
        }

        private void TryPatchGameClientChatRelay()
        {
            try
            {
                Type gameClientType = AccessTools.TypeByName("Barotrauma.Networking.GameClient");
                var targetMethod = gameClientType == null
                    ? null
                    : gameClientType
                        .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                        .FirstOrDefault(m =>
                        {
                            if (m.Name != "AddChatMessage") { return false; }
                            var p = m.GetParameters();
                            return p.Length == 1 &&
                                   p[0].ParameterType.FullName == "Barotrauma.Networking.ChatMessage";
                        });
                var prefixMethod = AccessTools.Method(typeof(MarkContainerChatRelayPatch), nameof(MarkContainerChatRelayPatch.Prefix));
                if (targetMethod == null || prefixMethod == null)
                {
                    LuaCsLogger.Log("[RetrieveItemsOrder] Skipping mark relay client chat patch; GameClient.AddChatMessage unavailable");
                    return;
                }

                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefixMethod));
                // LuaCsLogger.Log("[RetrieveItemsOrder] Patched client mark relay chat handler");
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to patch client mark relay chat handler: {ex}");
            }
        }

        public static void EnsureHudPatchFromClientContext()
        {
            TryPatchMarkedContainerHud("client-context");
        }

        private static void TryPatchMarkedContainerHud(string source)
        {
            if (attemptedHudPatch || harmony == null)
            {
                if (attemptedHudPatch)
                {
                    // LuaCsLogger.Log($"[RetrieveItemsOrder] HUD patch already attempted from {source}, applied={hudPatchApplied}");
                }
                return;
            }

            attemptedHudPatch = true;
            try
            {
                Type hudType = AccessTools.TypeByName("Barotrauma.CharacterHUD");
                // LuaCsLogger.Log($"[RetrieveItemsOrder] HUD patch lookup CharacterHUD from {source}: {(hudType != null ? hudType.FullName : "<null>")}");
                if (hudType == null)
                {
                    LuaCsLogger.Log("[RetrieveItemsOrder] Skipping marked-container HUD patch; CharacterHUD type unavailable");
                    return;
                }

                Type spriteBatchType = AccessTools.TypeByName("Microsoft.Xna.Framework.Graphics.SpriteBatch");
                // LuaCsLogger.Log($"[RetrieveItemsOrder] HUD patch lookup SpriteBatch from {source}: {(spriteBatchType != null ? spriteBatchType.FullName : "<null>")}");

                var targetMethod =
                    (spriteBatchType != null
                        ? AccessTools.Method(hudType, "Draw", new[] { spriteBatchType, typeof(Character), typeof(Camera) })
                        : null)
                    ?? hudType
                        .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                        .FirstOrDefault(m =>
                        {
                            if (m.Name != "Draw") { return false; }
                            var p = m.GetParameters();
                            return p.Length == 3 &&
                                   p[0].ParameterType.Name == "SpriteBatch" &&
                                   p[1].ParameterType.FullName == typeof(Character).FullName &&
                                   p[2].ParameterType.FullName == typeof(Camera).FullName;
                        });

                var postfixMethod = AccessTools.Method(typeof(MarkedContainerHudPatchShared), nameof(MarkedContainerHudPatchShared.Postfix));
                // LuaCsLogger.Log($"[RetrieveItemsOrder] HUD patch draw target from {source}: {targetMethod}");
                if (targetMethod != null)
                {
                    string parameterSummary = string.Join(", ", targetMethod.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name));
                    // LuaCsLogger.Log($"[RetrieveItemsOrder] HUD patch draw params from {source}: {parameterSummary}");
                }
                // LuaCsLogger.Log($"[RetrieveItemsOrder] HUD patch postfix from {source}: {postfixMethod}");
                if (targetMethod == null || postfixMethod == null)
                {
                    LuaCsLogger.Log("[RetrieveItemsOrder] Skipping marked-container HUD patch; draw method unavailable");
                    return;
                }

                harmony.Patch(targetMethod, postfix: new HarmonyMethod(postfixMethod));
                hudPatchApplied = true;
                // LuaCsLogger.Log($"[RetrieveItemsOrder] Patched marked-container HUD overlay: {targetMethod}");
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to patch marked-container HUD overlay: {ex}");
            }
        }

    }

    public static class RetrieveItemsIds
    {
        public static readonly Identifier OrderIdentifier = "retrieveitems".ToIdentifier();
        public static readonly Identifier WreckOrderIdentifier = "retrievewreckitems".ToIdentifier();
        public static readonly Identifier MarkContainerOrderIdentifier = "markretrievecontainer".ToIdentifier();
        public static readonly Identifier MarkedContainerTag = "retrieveitemsmarked".ToIdentifier();
        public static readonly Identifier SearchDialog = "retrieveitems.searching".ToIdentifier();
        public static readonly Identifier ReturnDialog = "retrieveitems.returning".ToIdentifier();
        public static readonly Identifier DepositDialog = "retrieveitems.depositing".ToIdentifier();
        public static readonly Identifier AbortDialog = "retrieveitems.abort".ToIdentifier();
        public static readonly Identifier SevereInjuryDialog = "retrieveitems.abort.tooinjured".ToIdentifier();
        public static readonly Identifier NoTargetDialog = "retrieveitems.abort.notarget".ToIdentifier();
        public static readonly Identifier CannotStoreDialog = "retrieveitems.abort.nostorage".ToIdentifier();
        public static readonly Identifier DoneDialog = "retrieveitems.done".ToIdentifier();
        public static readonly Identifier OrderReceivedDialog = "retrieveitems.orderreceived".ToIdentifier();
        public static readonly Identifier CancelDialog = "retrieveitems.cancelled".ToIdentifier();
        public static readonly Identifier RefuseDialog = "retrieveitems.refused".ToIdentifier();
        public static readonly Identifier HostilesDialog = "retrieveitems.hostiles".ToIdentifier();
        public static readonly Identifier MarkedDialog = "retrieveitems.marked".ToIdentifier();
        public static readonly Identifier UnmarkedDialog = "retrieveitems.unmarked".ToIdentifier();
    }

    public static class RetrieveItemsOrderRules
    {
        private const string MarkRelayPrefix = "__retrieveitems_mark__";
        private static readonly HashSet<Item> markedContainers = new HashSet<Item>();
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
            CleanupMarkedContainers();
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
            CleanupMarkedContainers();
            if (container == null)
            {
                return;
            }

            if (!marked)
            {
                markedContainers.Remove(container);
                RestoreContainerVisual(container);
                // LuaCsLogger.Log($"[RetrieveItemsOrder] Unmarked container state: {container.Name}");
                return;
            }

            markedContainers.Add(container);
            markVersions.TryGetValue(container, out int version);
            markVersions[container] = version + 1;
            container.ExternalHighlight = true;
            container.AddTag(RetrieveItemsIds.MarkedContainerTag);
            // LuaCsLogger.Log($"[RetrieveItemsOrder] Marked container state: {container.Name}");
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
            CleanupMarkedContainers();
            return container != null && markedContainers.Contains(container);
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
            CleanupMarkedContainers();
            return markedContainers
                .Where(container =>
                    container != null &&
                    !container.Removed &&
                    container.Submarine == targetOutpost)
                .Concat(Item.ItemList.Where(container =>
                    container != null &&
                    !container.Removed &&
                    container.Submarine == targetOutpost &&
                    container.HasTag(RetrieveItemsIds.MarkedContainerTag)))
                .Distinct()
                .ToList();
        }

        public static IEnumerable<Item> GetMarkedContainers()
        {
            CleanupMarkedContainers();
            return markedContainers
                .Where(container => container != null && !container.Removed)
                .Concat(Item.ItemList.Where(container =>
                    container != null &&
                    !container.Removed &&
                    container.HasTag(RetrieveItemsIds.MarkedContainerTag)))
                .Distinct()
                .ToList();
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

        private static void CleanupMarkedContainers()
        {
            foreach (Item container in markedContainers.Where(container => container == null || container.Removed).ToList())
            {
                RestoreContainerVisual(container);
                markedContainers.Remove(container);
            }
        }

        public static void ClearMarkedContainers()
        {
            foreach (Item container in markedContainers.ToList())
            {
                RestoreContainerVisual(container);
            }
            markedContainers.Clear();
        }

        private static void RestoreContainerVisual(Item container)
        {
            if (container == null || container.Removed)
            {
                return;
            }

            container.ExternalHighlight = false;
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
            if (!IsMarkableRetrievalTarget(container))
            {
                return false;
            }

            if (container.Submarine == null)
            {
                return IsPortableRetrievalTarget(container);
            }

            return !HasHostiles(container.Submarine, orderedCharacter);
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
                            RetrieveItemsOrderRules.GetText(RetrieveItemsIds.HostilesDialog, "I won't do that until the outpost is clear of hostiles."),
                            identifier: RetrieveItemsIds.HostilesDialog,
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

    internal static class MarkContainerChatRelayPatch
    {
        public static bool Prefix(object __0)
        {
            if (!RetrieveItemsOrderRules.TryHandleMarkContainerRelay(__0))
            {
                return true;
            }

            // LuaCsLogger.Log("[RetrieveItemsOrder] Consumed hidden mark relay chat message");
            return false;
        }
    }

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

    internal static class MarkedContainerHudPatchShared
    {
        private static readonly Vector2 MarkerOffset = new Vector2(0.0f, -24.0f);
        private const float MarkerScale = 0.5f;
        private static DateTime lastPassLog;

        public static void Postfix(object[] __args, Character character, Camera cam)
        {
            try
            {
                object spriteBatch = __args != null && __args.Length > 0 ? __args[0] : null;
                OrderPrefab prefab = RetrieveItemsOrderRules.GetOrderPrefab(RetrieveItemsIds.OrderIdentifier);
                object sprite = prefab?.SymbolSprite;
                if (sprite == null || spriteBatch == null || cam == null)
                {
                    return;
                }

                Item[] markedContainers = Item.ItemList
                    .Where(item =>
                        item != null &&
                        !item.Removed &&
                        item.HasTag(RetrieveItemsIds.MarkedContainerTag))
                    .ToArray();
                if (markedContainers.Length == 0)
                {
                    return;
                }

                if ((DateTime.UtcNow - lastPassLog).TotalSeconds >= 2.0)
                {
                    lastPassLog = DateTime.UtcNow;
                    // LuaCsLogger.Log($"[RetrieveItemsOrder] Shared HUD sees {markedContainers.Length} marked containers");
                }

                Type spriteType = sprite.GetType();
                Type spriteEffectsType = spriteType.Assembly.GetType("Microsoft.Xna.Framework.Graphics.SpriteEffects");
                if (spriteEffectsType == null)
                {
                    return;
                }

                object spriteEffectsNone = Enum.ToObject(spriteEffectsType, 0);
                var drawMethod = spriteType
                    .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "Draw") { return false; }
                        var p = m.GetParameters();
                        return p.Length == 7 &&
                               string.Equals(p[0].ParameterType.FullName, "Microsoft.Xna.Framework.Graphics.ISpriteBatch", StringComparison.Ordinal) &&
                               p[1].ParameterType == typeof(Vector2) &&
                               p[2].ParameterType == typeof(Color) &&
                               p[3].ParameterType == typeof(float) &&
                               p[4].ParameterType == typeof(float) &&
                               p[5].ParameterType == spriteEffectsType &&
                               Nullable.GetUnderlyingType(p[6].ParameterType) == typeof(float);
                    });

                if (drawMethod == null)
                {
                    return;
                }

                foreach (Item container in markedContainers)
                {
                    if (container == null || container.Removed || container.HiddenInGame)
                    {
                        continue;
                    }

                    Vector2 worldPos = container.DrawPosition + MarkerOffset;
                    Vector2 screenPos = cam.WorldToScreen(worldPos);

                    drawMethod.Invoke(sprite, new object[]
                    {
                        spriteBatch,
                        screenPos,
                        Color.White,
                        0.0f,
                        MarkerScale,
                        spriteEffectsNone,
                        0.0f
                    });
                }
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to draw shared HUD overlay: {ex}");
            }
        }
    }

    internal sealed class AIObjectiveRetrieveWreckItems : AIObjective
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
        private const float OpenWaterGridSize = 100.0f;
        private const float OpenWaterCloseEnough = 180.0f;
        private const float OpenWaterWaypointCloseEnough = 100.0f;
        private const float OpenWaterRepathInterval = 2.0f;
        private const float OpenWaterObstacleInflation = 40.0f;
        private const float OpenWaterNodeClearance = 40.0f;
        private const int OpenWaterNearestNodeSearchRadius = 20;

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
            if (ShouldUseOpenWaterFallback())
            {
                ReleaseOpenWaterMovementControl();
                ClearSubObjective();
                travelPhase = WreckTravelPhase.OpenWater;
                ReleaseExitAirlockDoorCommand();
                StartOpenWaterFallback();
                return;
            }

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

            OpenExitAirlockDoor(exitAirlockGap);
            Vector2 exitPoint = GetExternalExitPoint(exitAirlockHull, exitAirlockGap);
            ApplyAirlockExitMovement(deltaTime, exitPoint);
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

            door.ShouldBeOpen = true;
            door.IsOpen = true;
            door.BotsShouldKeepOpen = false;
            if (!exitAirlockDoorCommanded)
            {
                exitAirlockDoorCommanded = true;
                LuaCsLogger.Log($"[RetrieveItemsOrder] Wreck retrieval opened exit airlock door for {character.Name}: gap={gap.Name ?? "<unnamed>"}");
            }
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
                door.ShouldBeOpen = false;
                if (door.IsOpen)
                {
                    door.IsOpen = false;
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
                door.ShouldBeOpen = false;
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

        private bool IsOpenWaterTransitionHull(Hull hull)
        {
            if (hull == null)
            {
                return true;
            }

            if (hull.Submarine != homeSubmarine)
            {
                return false;
            }

            return GetHullWaterRatio(hull) > 0.90f;
        }

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

            if (currentSubObjective != null)
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
