using System.Collections.Generic;
using System.IO;
using System.Linq;
using BaseLib.Abstracts;
using BaseLib.Utils;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Runs;

namespace Test.Scripts;

/// <summary>
/// Social Strategy relic: at rest sites the holder can give unlimited Strike
/// or Defend cards to an ally. Both the unlimited count and the Strike/Defend
/// filter are applied via TryModifyRestSiteOptions so they compose cleanly
/// with other relics that also modify the Social option.
/// </summary>
[Pool(typeof(SharedRelicPool))]
public class SocialStrategyRelic : CustomRelicModel
{
    public override RelicRarity Rarity => RelicRarity.Common;

    // Only enter the relic pool (and be offered as a reward) in multiplayer runs.
    public override bool IsAllowed(IRunState runState) => runState.Players.Count > 1;

    // base class returns Array.Empty<DynamicVar>() by default; no override needed.

    // -------------------------------------------------------------------------
    // Icon — load from the mod's assets folder at runtime.
    // Without a PCK we can't use res:// paths, so we point directly at the
    // PNG on disk. Godot 4's ResourceLoader can load PNGs from absolute paths.
    // -------------------------------------------------------------------------
    private static string IconFilePath =>
        Path.Combine(
            Path.GetDirectoryName(typeof(SocialStrategyRelic).Assembly.Location)!,
            "assets", "social_strategy.png")
        .Replace('\\', '/');

    public override string PackedIconPath => IconFilePath;
    protected override string PackedIconOutlinePath => IconFilePath;
    protected override string BigIconPath => IconFilePath;

    // -------------------------------------------------------------------------
    // Effect: allow unlimited Strike/Defend card transfers at rest sites.
    // -------------------------------------------------------------------------
    public override bool TryModifyRestSiteOptions(
        Player player, ICollection<RestSiteOption> options)
    {
        var social = options.OfType<SocialRestSiteOption>().FirstOrDefault();
        if (social == null) return false; // solo run or option not present

        // Remove the 1-card limit.
        social.CardCount = int.MaxValue;

        // Restrict selection to Strike and Defend cards only.
        social.CardFilter = card =>
            card.Tags.Contains(CardTag.Strike) ||
            card.Tags.Contains(CardTag.Defend);

        return true;
    }
}
