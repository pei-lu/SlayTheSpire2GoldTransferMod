using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Runs;

namespace Test.Scripts;

public class SocialRestSiteOption : RestSiteOption
{
    private const string SocialStrategyRelicEntry = "SOCIAL_STRATAGY";

    public override string OptionId => "SOCIAL";
    public int CardCount { get; set; } = 1;
    public Func<CardModel, bool>? CardFilter { get; set; }
    public override IEnumerable<string> AssetPaths => NDeckCardSelectScreen.AssetPaths;

    public SocialRestSiteOption(Player owner) : base(owner)
    {
        IsEnabled = owner.RunState.Players.Count > 1 && owner.Deck.Cards.Count > 0;
    }

    public override async Task<bool> OnSelect()
    {
        return await ExecuteOnSelect();
    }

    private async Task<bool> ExecuteOnSelect()
    {
        bool hasSocialStrategy = HasSocialStrategyRelic(Owner);
        Func<CardModel, bool>? effectiveFilter = hasSocialStrategy
            ? static card => card.Tags.Contains(CardTag.Strike) || card.Tags.Contains(CardTag.Defend)
            : null;

        int maxCount = hasSocialStrategy ? Owner.Deck.Cards.Count : 1;
        var prefs = hasSocialStrategy
            ? new CardSelectorPrefs(
                new MegaCrit.Sts2.Core.Localization.LocString("card_selection", "TO_GIVE"),
                0,
                maxCount)
            {
                Cancelable = true,
                RequireManualConfirmation = true,
            }
            : new CardSelectorPrefs(
                new MegaCrit.Sts2.Core.Localization.LocString("card_selection", "TO_GIVE"),
                1)
            {
                Cancelable = true,
                RequireManualConfirmation = true,
            };

        var selected = (await CardSelectCmd.FromDeckForRemoval(Owner, prefs, effectiveFilter)).ToList();
        if (selected.Count == 0) return false;

        var recipient = await SelectRecipient();
        if (recipient == null) return false;

        foreach (var card in selected)
        {
            await CardPileCmd.RemoveFromDeck(card, showPreview: false);
            var copy = (CardModel)card.ClonePreservingMutability();
            copy.Owner = null!;
            Owner.RunState.AddCard(copy, recipient);
            await CardPileCmd.Add(copy, PileType.Deck);
        }

        return true;
    }

    private static bool HasSocialStrategyRelic(Player player)
    {
        if (player.GetRelic<SocialStrategyRelic>() != null) return true;
        return player.Relics.Any(static r => string.Equals(r.Id.Entry, SocialStrategyRelicEntry, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Player?> SelectRecipient()
    {
        uint choiceId = RunManager.Instance.PlayerChoiceSynchronizer.ReserveChoiceId(Owner);
        Player? target = null;

        if (LocalContext.IsMe(Owner))
        {
            NRestSiteRoom.Instance?.AnimateDescriptionDown();

            var buttonForOption = NRestSiteRoom.Instance?.GetButtonForOption(this);
            if (buttonForOption == null)
            {
                RunManager.Instance.PlayerChoiceSynchronizer
                    .SyncLocalChoice(Owner, choiceId, PlayerChoiceResult.FromPlayerId(null));
                return null;
            }

            var startPosition = buttonForOption.GlobalPosition + buttonForOption.Size / 2f;
            bool usingController = NControllerManager.Instance?.IsUsingController ?? false;
            var targetManager = NTargetManager.Instance;
            if (targetManager == null)
            {
                RunManager.Instance.PlayerChoiceSynchronizer
                    .SyncLocalChoice(Owner, choiceId, PlayerChoiceResult.FromPlayerId(null));
                return null;
            }

            targetManager.StartTargeting(
                TargetType.AnyPlayer,
                startPosition,
                usingController ? TargetMode.Controller : TargetMode.ClickMouseToTarget,
                ShouldCancelTargeting,
                AllowHoveringNode);

            if (usingController)
            {
                var characters = (NRestSiteRoom.Instance?.characterAnims ?? Enumerable.Empty<NRestSiteCharacter>())
                    .Where(c => c.Player != Owner)
                    .ToList();

                for (int i = 0; i < characters.Count; i++)
                {
                    characters[i].Hitbox.SetFocusMode(Control.FocusModeEnum.All);
                    characters[i].Hitbox.FocusNeighborTop = characters[i].Hitbox.GetPath();
                    characters[i].Hitbox.FocusNeighborBottom = characters[i].Hitbox.GetPath();
                    characters[i].Hitbox.FocusNeighborLeft = i <= 0
                        ? characters[characters.Count - 1].Hitbox.GetPath()
                        : characters[i - 1].Hitbox.GetPath();
                    characters[i].Hitbox.FocusNeighborRight = i < characters.Count - 1
                        ? characters[i + 1].Hitbox.GetPath()
                        : characters[0].Hitbox.GetPath();
                }

                characters.FirstOrDefault()?.Hitbox.TryGrabFocus();
            }

            target = NodeToPlayer(await targetManager.SelectionFinished());
            RunManager.Instance.PlayerChoiceSynchronizer
                .SyncLocalChoice(Owner, choiceId, PlayerChoiceResult.FromPlayerId(target?.NetId));
        }
        else
        {
            ulong? playerId = (await RunManager.Instance.PlayerChoiceSynchronizer
                .WaitForRemoteChoice(Owner, choiceId)).AsPlayerId();
            if (playerId.HasValue)
                target = Owner.RunState.GetPlayer(playerId.Value);
        }

        NRestSiteRoom.Instance?.AnimateDescriptionUp();
        return target;
    }

    private static Player? NodeToPlayer(Node? node)
    {
        if (node == null) return null;
        if (node is NMultiplayerPlayerState pState) return pState.Player;
        if (node is NRestSiteCharacter c) return c.Player;
        return null;
    }

    private static bool ShouldCancelTargeting()
    {
        if ((NOverlayStack.Instance?.ScreenCount ?? 0) <= 0)
            return NCapstoneContainer.Instance?.InUse ?? false;
        return true;
    }

    private static bool AllowHoveringNode(Node node)
    {
        return !LocalContext.IsMe(NodeToPlayer(node));
    }
}
