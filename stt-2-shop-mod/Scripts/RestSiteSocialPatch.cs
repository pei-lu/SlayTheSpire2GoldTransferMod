using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Runs;

namespace Test.Scripts;

// ---------------------------------------------------------------------------
// Patch 1 — inject the Social option into the rest-site option list.
//
// We hook Hook.ModifyRestSiteOptions (the method that iterates every relic/
// modifier via TryModifyRestSiteOptions) as a Prefix so our option is already
// in the collection when relics run. That lets any relic raise CardCount via:
//
//   public override bool TryModifyRestSiteOptions(Player player,
//       ICollection<RestSiteOption> options)
//   {
//       var social = options.OfType<SocialRestSiteOption>().FirstOrDefault();
//       if (social != null) { social.CardCount++; return true; }
//       return false;
//   }
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyRestSiteOptions))]
public static class RestSiteSocialOptionPatch
{
    // ReSharper disable once UnusedMember.Local
    static void Prefix(Player player, ICollection<RestSiteOption> options)
    {
        // Only add in multiplayer and only once (guard against double-patch).
        if (player.RunState.Players.Count > 1
            && !options.OfType<SocialRestSiteOption>().Any())
        {
            options.Add(new SocialRestSiteOption(player));
        }
    }
}

// ---------------------------------------------------------------------------
// Patch 2 — replace the icon on the rest-site button with social.png.
//
// NRestSiteButton.Reload() (private) sets _icon.Texture = Option.Icon.
// The base-class Icon property resolves from the game's preload cache using
// "ui/rest_site/option_social.png" which doesn't exist in the base game, so
// we override it here after Reload runs.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(NRestSiteButton), "Reload")]
public static class RestSiteButtonIconPatch
{
    private static readonly FieldInfo _iconField =
        typeof(NRestSiteButton).GetField("_icon",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static ImageTexture? _cachedTexture;

    // ReSharper disable once UnusedMember.Local
    static void Postfix(NRestSiteButton __instance)
    {
        if (__instance.Option is not SocialRestSiteOption) return;

        var icon = _iconField.GetValue(__instance) as TextureRect;
        if (icon == null) return;

        _cachedTexture ??= LoadSocialTexture();
        if (_cachedTexture != null)
            icon.Texture = _cachedTexture;
    }

    private static ImageTexture? LoadSocialTexture()
    {
        try
        {
            var modDir = Path.GetDirectoryName(
                typeof(RestSiteButtonIconPatch).Assembly.Location) ?? "";
            var path = Path.Combine(modDir, "assets", "social.png");
            if (!File.Exists(path)) return null;
            var img = Image.LoadFromFile(path);
            return ImageTexture.CreateFromImage(img);
        }
        catch (System.Exception ex)
        {
            Log.Debug($"[sts2-shop-mod] Failed to load social.png: {ex.Message}");
            return null;
        }
    }
}

// ---------------------------------------------------------------------------
// Patch 3 — provide a fallback Icon for SOCIAL option itself.
//
// NRestSiteCharacter thought-bubble uses RestSiteOption.Icon directly. If
// OPTION_SOCIAL icon path is missing in the base preload cache, Icon can be
// null and first click throws before OnSelect runs.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(RestSiteOption), "get_Icon")]
public static class RestSiteOptionIconFallbackPatch
{
    private static ImageTexture? _cachedTexture;

    // ReSharper disable once UnusedMember.Local
    static void Postfix(RestSiteOption __instance, ref Texture2D __result)
    {
        if (__instance is not SocialRestSiteOption) return;
        if (__result != null) return;

        _cachedTexture ??= LoadSocialTexture();
        if (_cachedTexture != null)
            __result = _cachedTexture;
    }

    private static ImageTexture? LoadSocialTexture()
    {
        try
        {
            var modDir = Path.GetDirectoryName(
                typeof(RestSiteOptionIconFallbackPatch).Assembly.Location) ?? "";
            var path = Path.Combine(modDir, "assets", "social.png");
            if (!File.Exists(path)) return null;
            var img = Image.LoadFromFile(path);
            return ImageTexture.CreateFromImage(img);
        }
        catch (System.Exception ex)
        {
            Log.Debug($"[sts2-shop-mod] Failed to load social icon fallback: {ex.Message}");
            return null;
        }
    }
}
