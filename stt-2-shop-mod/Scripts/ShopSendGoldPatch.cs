using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace Test.Scripts;

/// <summary>
/// Patches NMerchantInventory to inject a donate-icon button next to the
/// "Remove Card" slot. In multiplayer, clicking it opens a dialog to send
/// gold to an ally (with a configurable fee from MoneyExchangeMod.json).
/// </summary>
[HarmonyPatch(typeof(NMerchantInventory), "_Ready")]
public static class ShopSendGoldPatch
{
    private const string ButtonNodeName = "SendGoldButton";

    // Fee is read directly from ShopModConfig, which BaseLib keeps in sync.
    private static int Fee => ShopModConfig.Fee;

    // -------------------------------------------------------------------------
    // Harmony patch
    // -------------------------------------------------------------------------

    // Harmony discovers this via reflection — suppress the "unused" hint
    // ReSharper disable once UnusedMember.Local
    static void Postfix(NMerchantInventory __instance)
    {
        GD.Print("[sts2-shop-mod] NMerchantInventory._Ready() patched — setting up Send Gold button");

        if (__instance.GetNodeOrNull(ButtonNodeName) != null)
            return;

        // Find the card-removal slot — we anchor our button next to it.
        if (__instance.GetNodeOrNull("%MerchantCardRemoval") is not Control cardRemoval)
        {
            GD.Print("[sts2-shop-mod] %MerchantCardRemoval node not found — button skipped");
            return;
        }

        GD.Print($"[sts2-shop-mod] Card removal node found at {cardRemoval.GlobalPosition}, adding button");

        // Load donate.png from the mod's assets folder.
        var modDir = Path.GetDirectoryName(typeof(ShopSendGoldPatch).Assembly.Location) ?? "";
        var imagePath = Path.Combine(modDir, "assets", "donate.png");
        var texture = LoadTexture(imagePath);

        // Build a TextureButton the same height as the card-removal slot.
        var button = new TextureButton
        {
            Name = ButtonNodeName,
            IgnoreTextureSize = true,
            StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(64, 64), // fallback size; overridden below
            TooltipText = "Send Gold to Ally",
        };

        if (texture != null)
            button.TextureNormal = texture;

        // Add to the same parent as cardRemoval so the button moves with the
        // open/close animation (SlotsContainer starts at Y=-1000 and tweens in).
        // Using local Position keeps it anchored correctly regardless of animation state.
        var slotParent = cardRemoval.GetParent<Control>();
        if (slotParent == null) return;
        slotParent.AddChild(button);

        // Defer one frame so the layout pass has set cardRemoval.Size correctly.
        Action oneShot = null!;
        oneShot = () =>
        {
            if (GodotObject.IsInstanceValid(cardRemoval) && GodotObject.IsInstanceValid(button))
            {
                float h = cardRemoval.Size.Y > 0 ? cardRemoval.Size.Y : 64f;
                button.CustomMinimumSize = new Vector2(h, h);

                // LOCAL position relative to shared parent — moves with the animation.
                button.Position = cardRemoval.Position + new Vector2(cardRemoval.Size.X + 8f, 0f);
                GD.Print($"[sts2-shop-mod] Button placed at local {button.Position} (cardRemoval local {cardRemoval.Position}, size {cardRemoval.Size})");
            }
            __instance.GetTree().ProcessFrame -= oneShot;
        };
        __instance.GetTree().ProcessFrame += oneShot;

        button.Pressed += () => OnButtonPressed(__instance, button);
    }

    // -------------------------------------------------------------------------
    // Button handler
    // -------------------------------------------------------------------------

    private static void OnButtonPressed(NMerchantInventory shopNode, TextureButton button)
    {
        var player = shopNode.Inventory?.Player;
        if (player == null) return;

        var others = player.RunState.Players
            .Where(p => p != player)
            .ToArray();

        if (others.Length == 0)
        {
            button.Visible = false; // no allies — hide permanently
            return;
        }

        if (player.Gold <= 0) return;

        OpenTransferDialog(shopNode, player, others);
    }

    // -------------------------------------------------------------------------
    // Dialog
    // -------------------------------------------------------------------------

    private static void OpenTransferDialog(NMerchantInventory shopNode, Player sender, Player[] recipients)
    {
        int windowHeight = 220 + recipients.Length * 44;

        var popup = new Window
        {
            Title = "Send Gold to Ally",
            InitialPosition = Window.WindowInitialPosition.CenterMainWindowScreen,
            Size = new Vector2I(300, windowHeight),
            Unresizable = true,
            Transient = true,
        };
        popup.CloseRequested += popup.QueueFree;

        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        popup.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(vbox);

        vbox.AddChild(new Label { Text = $"Your gold: {sender.Gold}" });
        vbox.AddChild(new Label { Text = "Amount to send:" });

        var spinner = new SpinBox
        {
            MinValue = 1,
            MaxValue = sender.Gold,
            Value = Math.Min(10, sender.Gold),
            Step = 1,
        };
        vbox.AddChild(spinner);

        // Live preview updates as the spinner changes.
        var previewLabel = new Label();
        UpdatePreview(previewLabel, (int)spinner.Value);
        spinner.ValueChanged += v => UpdatePreview(previewLabel, (int)v);
        vbox.AddChild(previewLabel);

        vbox.AddChild(new HSeparator());
        vbox.AddChild(new Label { Text = "Send to:" });

        var gs = GetGameService();
        foreach (var target in recipients)
        {
            string name = gs != null
                ? PlatformUtil.GetPlayerName(gs.Platform, target.NetId)
                : $"Player {sender.RunState.GetPlayerSlotIndex(target) + 1}";
            var sendBtn = new Button { Text = $"{name}  ({target.Gold} gold)" };
            var capturedTarget = target;
            sendBtn.Pressed += () =>
            {
                int amount = (int)spinner.Value;
                if (amount <= 0 || amount > sender.Gold) return;
                popup.QueueFree();
                TaskHelper.RunSafely(TransferGold(sender, capturedTarget, amount));
            };
            vbox.AddChild(sendBtn);
        }

        vbox.AddChild(new HSeparator());
        var cancelBtn = new Button { Text = "Cancel" };
        cancelBtn.Pressed += popup.QueueFree;
        vbox.AddChild(cancelBtn);

        shopNode.GetTree().Root.AddChild(popup);
        popup.PopupCentered();
    }

    private static void UpdatePreview(Label label, int amount)
    {
        int received = CalcReceived(amount);
        label.Text = Fee > 0
            ? $"Recipient gets: {received} gold  (fee: {Fee}%)"
            : $"Recipient gets: {received} gold";
    }

    // -------------------------------------------------------------------------
    // Transfer logic
    // -------------------------------------------------------------------------

    private static async Task TransferGold(Player sender, Player recipient, int amount)
    {
        if (sender.Gold < amount) return;
        int received = CalcReceived(amount);
        await PlayerCmd.LoseGold(amount, sender, GoldLossType.Spent);
        if (received <= 0) return;

        // Update gold on local machine for both players
        await PlayerCmd.GainGold(received, recipient);

        // In multiplayer, broadcast a RewardObtainedMessage spoofed as coming from the
        // recipient so that all other clients also credit the gold to the right player.
        if (!RunManager.Instance.IsSinglePlayerOrFakeMultiplayer)
            SyncGoldGainForRecipient(recipient, received);
    }

    private static void SyncGoldGainForRecipient(Player recipient, int goldAmount)
    {
        var gs = GetGameService();
        if (gs == null)
        {
            GD.PrintErr("[sts2-shop-mod] SyncGoldGainForRecipient: could not get game service");
            return;
        }

        // Build a gold-reward message. The location field is unused by the gold branch
        // of HandleRewardObtainedMessage, so a default value is fine here.
        var message = new RewardObtainedMessage
        {
            rewardType = RewardType.Gold,
            goldAmount = goldAmount,
            wasSkipped = false,
            location = default,
        };

        // Serialize with recipient's NetId as the "sender" so the remote handler
        // calls GainGold for the correct player when it processes the packet.
        var messageBus = new NetMessageBus();
        byte[] bytes = messageBus.SerializeMessage(recipient.NetId, message, out int length);

        if (gs is NetHostGameService hostService)
        {
            // We are the host — push the packet directly to every ready client.
            if (hostService.NetHost == null) return;
            foreach (var peer in hostService.ConnectedPeers)
            {
                if (peer.readyForBroadcasting)
                    hostService.NetHost.SendMessageToClient(peer.peerId, bytes, length, message.Mode);
            }
        }
        else if (gs is NetClientGameService clientService)
        {
            // We are a client — send to the host; it will broadcast + handle locally.
            clientService.NetClient?.SendMessageToHost(bytes, length, message.Mode);
        }
    }

    private static INetGameService? GetGameService()
    {
        var sync = RunManager.Instance.RewardSynchronizer;
        if (sync == null) return null;
        var gsField = typeof(RewardSynchronizer)
            .GetField("_gameService", BindingFlags.NonPublic | BindingFlags.Instance);
        return gsField?.GetValue(sync) as INetGameService;
    }

    /// <summary>Fee is ceiling-rounded; recipient gets amount minus that fee.</summary>
    private static int CalcReceived(int amount)
    {
        int feeGold = (int)Math.Ceiling(amount * (Fee / 100.0));
        return amount - feeGold;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ImageTexture? LoadTexture(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var img = Image.LoadFromFile(path);
            return ImageTexture.CreateFromImage(img);
        }
        catch (Exception ex)
        {
            Log.Debug($"[ShopSendGold] Failed to load texture '{path}': {ex.Message}");
            return null;
        }
    }
}
