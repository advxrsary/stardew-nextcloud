using System;
using System.Collections.Generic;
using System.Linq;
using CloudSync.Sync;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace CloudSync.Patches
{
    /// <summary>
    /// Harmony postfix on SaveFileSlot.Draw to render a cloud icon
    /// for saves that exist on the remote Nextcloud server.
    /// </summary>
    internal static class SaveFileSlotPatch
    {
        private static CloudSaveChecker? Checker;
        private static IMonitor? Monitor;
        private static Texture2D? CloudTexture;
        private static Rectangle CloudSourceRect;
        private static bool SpriteReady;
        private static bool DebugLogged;

        /// <summary>Initialize the patch with dependencies.</summary>
        public static void Init(CloudSaveChecker checker, IMonitor monitor)
        {
            Checker = checker;
            Monitor = monitor;
        }

        /// <summary>Apply the Harmony patch.</summary>
        public static void Apply(Harmony harmony)
        {
            try
            {
                var targetType = typeof(LoadGameMenu).GetNestedType(
                    "SaveFileSlot",
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic
                );

                if (targetType == null)
                {
                    Monitor?.Log(
                        "CloudSync: SaveFileSlot type not found, skipping patch",
                        LogLevel.Warn
                    );
                    return;
                }

                var drawMethod = targetType.GetMethod(
                    "Draw",
                    new[] { typeof(SpriteBatch), typeof(int) }
                );

                if (drawMethod == null)
                {
                    Monitor?.Log(
                        "CloudSync: SaveFileSlot.Draw method not found, skipping patch",
                        LogLevel.Warn
                    );
                    return;
                }

                var postfix = new HarmonyMethod(
                    typeof(SaveFileSlotPatch).GetMethod(
                        nameof(DrawPostfix),
                        System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.NonPublic
                    )
                );

                harmony.Patch(drawMethod, postfix: postfix);
                Monitor?.Log("CloudSync: SaveFileSlot.Draw patch applied", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Monitor?.Log(
                    $"CloudSync: failed to apply SaveFileSlot patch: {ex.Message}",
                    LogLevel.Error
                );
            }
        }

        /// <summary>Load the cloud sprite from the furniture tilesheet.</summary>
        public static void LoadSprite()
        {
            try
            {
                // Look up Cloud_Decal in furniture data at runtime
                var furnitureData = Game1.content.Load<Dictionary<string, string>>(
                    "Data\\Furniture"
                );

                string? cloudId = null;
                foreach (var kvp in furnitureData)
                {
                    // Furniture data format: Name/Type/TilesheetSize/...
                    string name = kvp.Value.Split('/')[0];
                    if (name.Equals("Cloud Decal", StringComparison.OrdinalIgnoreCase))
                    {
                        cloudId = kvp.Key;
                        break;
                    }
                }

                if (cloudId == null)
                {
                    Monitor?.Log(
                        "CloudSync: Cloud Decal not found in furniture data, " +
                        "using fallback sprite",
                        LogLevel.Debug
                    );
                    UseFallbackSprite();
                    return;
                }

                // Use ItemRegistry to get parsed data with texture and source rect
                var itemData = ItemRegistry.GetDataOrErrorItem($"(F){cloudId}");
                CloudTexture = itemData.GetTexture();
                CloudSourceRect = itemData.GetSourceRect();
                SpriteReady = true;

                Monitor?.Log(
                    $"CloudSync: loaded Cloud Decal sprite (ID: {cloudId}, " +
                    $"rect: {CloudSourceRect})",
                    LogLevel.Debug
                );
            }
            catch (Exception ex)
            {
                Monitor?.Log(
                    $"CloudSync: failed to load Cloud Decal sprite: {ex.Message}",
                    LogLevel.Warn
                );
                UseFallbackSprite();
            }
        }

        /// <summary>Fallback: use a cloud-like icon from the Cursors sheet.</summary>
        private static void UseFallbackSprite()
        {
            try
            {
                // Use rain icon from Cursors as fallback (small cloud shape)
                CloudTexture = Game1.mouseCursors;
                CloudSourceRect = new Rectangle(346, 392, 8, 8);
                SpriteReady = true;
                Monitor?.Log("CloudSync: using fallback cloud sprite", LogLevel.Debug);
            }
            catch
            {
                SpriteReady = false;
            }
        }

        /// <summary>Harmony postfix: draw cloud icon after slot render.</summary>
        /// <remarks>
        /// Uses Harmony's ___fieldName convention to access the protected 'menu' field
        /// from the base MenuSlot class without reflection.
        /// </remarks>
        // ReSharper disable InconsistentNaming
        private static void DrawPostfix(
            object __instance, SpriteBatch b, int i, LoadGameMenu ___menu)
        {
            if (!SpriteReady || Checker == null || !Checker.IsLoaded || ___menu == null)
            {
                if (!DebugLogged)
                {
                    Monitor?.Log(
                        $"CloudSync DrawPostfix: early exit " +
                        $"(sprite={SpriteReady}, checker={Checker != null}, " +
                        $"loaded={Checker?.IsLoaded}, menu={___menu != null})",
                        LogLevel.Debug
                    );
                    DebugLogged = true;
                }
                return;
            }

            try
            {
                var farmerField = __instance.GetType().GetField("Farmer");
                if (farmerField == null)
                {
                    if (!DebugLogged)
                    {
                        Monitor?.Log("CloudSync DrawPostfix: Farmer field not found", LogLevel.Debug);
                        DebugLogged = true;
                    }
                    return;
                }

                var farmer = farmerField.GetValue(__instance) as Farmer;
                if (farmer == null)
                    return;

                long farmerId = farmer.UniqueMultiplayerID;

                if (!DebugLogged)
                {
                    Monitor?.Log(
                        $"CloudSync DrawPostfix: farmer='{farmer.Name}', " +
                        $"multiplayerId={farmerId}, " +
                        $"match={Checker.HasCloudSaveForFarmer(farmerId)}, " +
                        $"cloud saves=[{string.Join(", ", Checker.GetAllSaves())}]",
                        LogLevel.Debug
                    );
                    DebugLogged = true;
                }

                if (!Checker.HasCloudSaveForFarmer(farmerId))
                    return;

                if (i < 0 || i >= ___menu.slotButtons.Count)
                    return;
                if (i >= ___menu.deleteButtons.Count)
                    return;

                // Position cloud icon to the left of the delete button
                var deleteBtn = ___menu.deleteButtons[i].bounds;
                float iconScale = 1.5f;
                int iconWidth = (int)(CloudSourceRect.Width * iconScale);
                int iconHeight = (int)(CloudSourceRect.Height * iconScale);
                int xPos = deleteBtn.X - iconWidth - 8;
                int yPos = deleteBtn.Y + (deleteBtn.Height - iconHeight) / 2 - 2;

                b.Draw(
                    CloudTexture,
                    new Vector2(xPos, yPos),
                    CloudSourceRect,
                    Color.White * 0.8f,
                    0f,
                    Vector2.Zero,
                    iconScale,
                    SpriteEffects.None,
                    0.99f
                );

                // Show tooltip on hover
                var iconRect = new Rectangle(xPos, yPos, iconWidth, iconHeight);
                if (iconRect.Contains(Game1.getMouseX(), Game1.getMouseY()))
                {
                    IClickableMenu.drawHoverText(
                        b, "Synced to cloud", Game1.smallFont
                    );
                }
            }
            catch
            {
                // Silently fail to avoid breaking the load screen
            }
        }
    }
}
