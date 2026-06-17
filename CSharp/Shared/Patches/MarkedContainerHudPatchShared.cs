using System;
using System.Linq;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace RetrieveItemsOrderMod
{
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
}
