using System;
using System.Linq;
using System.Reflection;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace RetrieveItemsOrderMod
{
    internal static class MarkedContainerHudPatchShared
    {
        private const float MarkerScale = 0.75f;
        private const float MaxDrawDistance = 450.0f;

        private static MethodInfo spriteBatchDrawTexture;
        private static object spriteEffectsNone;
        private static bool methodsResolved;
        private static int lastLoggedCount = -1;

        public static void Postfix(object[] __args, Character character, Camera cam)
        {
            try
            {
                if (character == null || cam == null || __args == null || __args.Length == 0)
                {
                    return;
                }

                OrderPrefab prefab = RetrieveItemsOrderRules.GetOrderPrefab(RetrieveItemsIds.MarkedContainerHudIdentifier);
                if (prefab?.SymbolSprite == null)
                {
                    return;
                }

                int markedCount = Item.ItemList.Count(item =>
                    item != null &&
                    !item.Removed &&
                    item.HasTag(RetrieveItemsIds.MarkedContainerTag));

                if (markedCount != lastLoggedCount)
                {
                    lastLoggedCount = markedCount;
                    LuaCsLogger.Log($"[RetrieveItemsOrder] HUD postfix running: {markedCount} marked containers");
                }

                if (markedCount == 0)
                {
                    return;
                }

                object spriteBatch = __args[0];
                if (spriteBatch == null)
                {
                    return;
                }

                if (!methodsResolved)
                {
                    methodsResolved = true;
                    spriteBatchDrawTexture = ResolveSpriteBatchDrawMethod();
                    Type spriteEffectsType = AccessTools.TypeByName("Microsoft.Xna.Framework.Graphics.SpriteEffects");
                    if (spriteEffectsType != null)
                    {
                        spriteEffectsNone = Enum.ToObject(spriteEffectsType, 0);
                    }
                    LuaCsLogger.Log($"[RetrieveItemsOrder] SpriteBatch.Draw resolved: {(spriteBatchDrawTexture != null ? "yes" : "no")}, SpriteEffects: {(spriteEffectsNone != null ? "yes" : "no")}");
                }

                if (spriteBatchDrawTexture == null || spriteEffectsNone == null)
                {
                    return;
                }

                Sprite sprite = prefab.SymbolSprite;

                object texture = AccessTools.Property(sprite.GetType(), "Texture")?.GetValue(sprite)
                    ?? AccessTools.Field(sprite.GetType(), "texture")?.GetValue(sprite)
                    ?? AccessTools.Field(sprite.GetType(), "Texture")?.GetValue(sprite);
                if (texture == null)
                {
                    LuaCsLogger.Log("[RetrieveItemsOrder] Sprite texture not found via reflection");
                    return;
                }

                // LuaCsLogger.Log($"[RetrieveItemsOrder] Texture type: {texture.GetType().FullName}, sprite size: {sprite.size}");

                int drawnCount = 0;
                foreach (Item container in Item.ItemList)
                {
                    if (container == null || container.Removed || container.HiddenInGame)
                    {
                        continue;
                    }

                    if (!container.HasTag(RetrieveItemsIds.MarkedContainerTag))
                    {
                        continue;
                    }

                    if (container.Submarine != character.Submarine)
                    {
                        continue;
                    }

                    float dist = Vector2.Distance(character.WorldPosition, container.WorldPosition);
                    float alpha = Math.Min((MaxDrawDistance - dist) / MaxDrawDistance * 2.0f, 1.0f);
                    if (alpha <= 0.0f)
                    {
                        continue;
                    }

                    Vector2 screenPos = cam.WorldToScreen(container.DrawPosition);
                    float symbolScale = Math.Min(64.0f / sprite.size.X, 1.0f) * MarkerScale;
                    Vector2 origin = new Vector2(sprite.size.X / 2.0f, sprite.size.Y / 2.0f);

                    try
                    {
                        spriteBatchDrawTexture.Invoke(spriteBatch, new object[]
                        {
                            texture,
                            screenPos,
                            null,
                            Color.White * alpha,
                            0.0f,
                            origin,
                            symbolScale,
                            spriteEffectsNone,
                            0.0f
                        });
                        drawnCount++;
                    }
                    catch (TargetInvocationException tie)
                    {
                        LuaCsLogger.Log($"[RetrieveItemsOrder] Draw invoke error: {tie.InnerException?.Message ?? tie.Message}");
                        spriteBatchDrawTexture = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        LuaCsLogger.Log($"[RetrieveItemsOrder] Draw invoke error: {ex.Message}");
                        spriteBatchDrawTexture = null;
                        break;
                    }
                }

                if (drawnCount > 0)
                {
                    // LuaCsLogger.Log($"[RetrieveItemsOrder] Drew {drawnCount} icons");
                }
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to draw shared HUD overlay: {ex}");
            }
        }

        private static MethodInfo ResolveSpriteBatchDrawMethod()
        {
            try
            {
                Type sbType = AccessTools.TypeByName("Microsoft.Xna.Framework.Graphics.SpriteBatch");
                if (sbType == null)
                {
                    LuaCsLogger.Log("[RetrieveItemsOrder] SpriteBatch type not found");
                    return null;
                }

                var candidates = sbType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(m => m.Name == "Draw")
                    .ToList();

                LuaCsLogger.Log($"[RetrieveItemsOrder] Found {candidates.Count} SpriteBatch.Draw overloads");

                foreach (var method in candidates)
                {
                    var p = method.GetParameters();
                    string parms = string.Join(", ", p.Select(px => $"{px.ParameterType.Name} {px.Name}"));
                    LuaCsLogger.Log($"[RetrieveItemsOrder]   Draw({parms})");

                    if (p.Length == 9 &&
                        p[0].ParameterType.Name == "Texture2D" &&
                        p[1].ParameterType.Name == "Vector2" &&
                        p[2].ParameterType.Name.Contains("Nullable") &&
                        p[3].ParameterType.Name == "Color" &&
                        p[4].ParameterType.Name == "Single" &&
                        p[5].ParameterType.Name == "Vector2" &&
                        p[6].ParameterType.Name == "Single" &&
                        p[7].ParameterType.Name == "SpriteEffects" &&
                        p[8].ParameterType.Name == "Single")
                    {
                        LuaCsLogger.Log("[RetrieveItemsOrder] Selected SpriteBatch.Draw(Texture2D,Vector2,Rectangle?,Color,float,Vector2,float,SpriteEffects,float)");
                        return method;
                    }
                }

                LuaCsLogger.Log("[RetrieveItemsOrder] Matching SpriteBatch.Draw not found");
                return null;
            }
            catch (Exception ex)
            {
                LuaCsLogger.Log($"[RetrieveItemsOrder] Failed to resolve SpriteBatch.Draw: {ex}");
                return null;
            }
        }
    }
}
