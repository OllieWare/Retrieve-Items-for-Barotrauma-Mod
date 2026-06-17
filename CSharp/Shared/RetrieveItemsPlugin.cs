using System;
using System.Linq;
using Barotrauma;
using HarmonyLib;

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
}
