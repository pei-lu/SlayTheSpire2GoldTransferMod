# Slay the Spire 2 — Modding API Reference

Game engine: **Godot 4.5.1 / C#**. Mods are `.dll` files loaded from `<game>/mods/<mod-id>/`.

---

## Project Setup

### Manifest (`<mod-id>.json`)
```json
{
  "id": "my-mod",           // must match <AssemblyName> in .csproj
  "name": "My Mod",
  "author": "you",
  "version": "1.0",
  "has_pck": false,
  "has_dll": true,
  "dependencies": ["BaseLib"],   // load order; omit if no deps
  "affects_gameplay": true
}
```

### .csproj key settings
```xml
<AssemblyName>my-mod</AssemblyName>   <!-- must match manifest "id" -->
<Sts2Dir>D:\SteamLibrary\steamapps\common\Slay the Spire 2</Sts2Dir>
<Sts2DataDir>$(Sts2Dir)\data_sts2_windows_x86_64</Sts2DataDir>

<Reference Include="sts2">
  <HintPath>$(Sts2DataDir)\sts2.dll</HintPath>
  <Private>false</Private>
</Reference>
<Reference Include="0Harmony">
  <HintPath>$(Sts2DataDir)\0Harmony.dll</HintPath>
  <Private>false</Private>
</Reference>
<Reference Include="BaseLib">
  <HintPath>$(Sts2Dir)\mods\BaseLib.0.2.0\BaseLib.dll</HintPath>
  <Private>false</Private>
</Reference>
<PackageReference Include="Alchyr.Sts2.ModAnalyzers" Version="*" PrivateAssets="all" />
```

---

## Mod Entry Point

**Namespace:** `MegaCrit.Sts2.Core.Modding`

```csharp
[ModInitializer("Init")]        // string must match method name below
public class Entry
{
    public static void Init()
    {
        var harmony = new Harmony("com.yourname.modid");
        harmony.PatchAll();                                      // auto-discover all patches
        ScriptManagerBridge.LookupScriptsInAssembly(typeof(Entry).Assembly);
        ModConfigRegistry.Register("my-mod", new MyModConfig()); // optional
        Log.Debug("[my-mod] loaded");
    }
}
```

---

## HarmonyLib Patching

**Package:** `0Harmony.dll` (ships with game)

```csharp
// Postfix — runs AFTER original method; __instance is the target object
[HarmonyPatch(typeof(NMerchantInventory), "_Ready")]
public static class MyPatch
{
    static void Postfix(NMerchantInventory __instance)
    {
        // modify state after original runs
    }
}

// Prefix — runs BEFORE; return false to skip original
[HarmonyPatch(typeof(SomeClass), nameof(SomeClass.SomeMethod))]
public static class MyPrefixPatch
{
    static bool Prefix(SomeClass __instance, ref int __result)
    {
        __result = 42;
        return false; // skip original
    }
}
```

**Tips:**
- `harmony.PatchAll()` auto-discovers all `[HarmonyPatch]` classes in the assembly.
- Use `__instance` to access the target object.
- Use `ref __result` to override the return value.
- Use `__0`, `__1`, … or parameter names to access method arguments.

---

## BaseLib Config

**Namespace:** `BaseLib.Config`

```csharp
public class MyModConfig : SimpleModConfig
{
    [SliderRange(0, 100, 1)]
    public static int Fee { get; set; } = 10;

    [SliderRange(1, 999, 1)]
    [SliderLabelFormat("{0} gold")]
    public static int MaxTransfer { get; set; } = 100;
}

// Registration (in Init)
ModConfigRegistry.Register("my-mod", new MyModConfig());

// Reading a value anywhere
int fee = MyModConfig.Fee;
```

**Config attributes:**

| Attribute | Purpose |
|-----------|---------|
| `[SliderRange(min, max, step)]` | Renders as a slider |
| `[SliderLabelFormat(format)]` | Formats the displayed value (`{0}` = value) |
| `[ConfigSection(name)]` | Adds a section header above this property |
| `[ConfigIgnore]` | Skip this property entirely |
| `[ConfigHideInUI]` | Persist but don't show in UI |
| `[HoverTipsByDefault]` | Add hover tooltips to all properties |

---

## Player & Run State

**Namespaces:** `MegaCrit.Sts2.Core.Entities.Players`, `MegaCrit.Sts2.Core.Runs`

### Player
```csharp
Player player = ...;

int gold       = player.Gold;
ulong netId    = player.NetId;         // Steam ID in online play
IRunState run  = player.RunState;
var relics     = player.Relics;        // IReadOnlyList<RelicModel>
var potions    = player.PotionSlots;   // IReadOnlyList<PotionModel?>
```

### RunState / RunManager
```csharp
// Access from anywhere
RunManager rm = RunManager.Instance;
bool running  = rm.IsInProgress;
IRunState rs  = rm.State;             // null if no run active

// Players
IReadOnlyList<Player> players = rs.Players;
Player local = rs.Players.First(p => p.NetId == myNetId);

// Map position
int act       = rs.CurrentActIndex;
MapCoord? pos = rs.CurrentMapCoord;
```

### IPlayerCollection helpers
```csharp
Player p  = playerCollection.GetPlayer(netId);       // by NetId
int slot  = playerCollection.GetPlayerSlotIndex(p);  // 0-based index
```

---

## Gold Commands

**Namespace:** `MegaCrit.Sts2.Core.Commands`

```csharp
await PlayerCmd.GainGold(amount, player);
await PlayerCmd.LoseGold(amount, player, GoldLossType.Spent);
await PlayerCmd.SetGold(amount, player);
```

**GoldLossType:** `None` | `Spent` | `Lost` | `Stolen`

> **Multiplayer caveat:** `GainGold` / `LoseGold` update local state only.
> After calling them, sync across the network using `RewardSynchronizer` (see below).

---

## Multiplayer Networking

**Namespace:** `MegaCrit.Sts2.Core.Multiplayer`

### Checking multiplayer state
```csharp
bool solo = RunManager.Instance.IsSinglePlayerOrFakeMultiplayer;
INetGameService gs = RunManager.Instance.NetService;
bool isHost = gs is NetHostGameService; // or gs.Type == NetGameType.Host
```

### Syncing a gold gain (correct pattern from GoldReward.cs)
```csharp
// Called on the LOCAL player's machine after PlayerCmd.GainGold:
RunManager.Instance.RewardSynchronizer.SyncLocalObtainedGold(amount);
// Internally: builds RewardObtainedMessage and calls _gameService.SendMessage(msg)
// Receiving clients look up the player by senderId and call GainGold locally.
```

### Sending a reward message spoofed as another player
When you need remote clients to award gold to a player who is *not* the local machine's player (e.g., sending gold to an ally), you must serialize the packet with the recipient's `NetId` as the `senderId`:

```csharp
using System.Reflection;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Rewards;

var message = new RewardObtainedMessage
{
    rewardType = RewardType.Gold,
    goldAmount = received,
    wasSkipped = false,
    location   = default,       // unused by gold branch of HandleRewardObtainedMessage
};

// Get _gameService (private field) from RewardSynchronizer
var sync = RunManager.Instance.RewardSynchronizer;
var gsField = typeof(RewardSynchronizer)
    .GetField("_gameService", BindingFlags.NonPublic | BindingFlags.Instance);
var gs = gsField?.GetValue(sync) as INetGameService;

// Serialize with recipient's NetId so remote HandleRewardObtainedMessage
// calls GetPlayer(senderId) and finds the right player.
var bus   = new NetMessageBus();
byte[] bytes = bus.SerializeMessage(recipient.NetId, message, out int length);

if (gs is NetHostGameService host)
{
    foreach (var peer in host.ConnectedPeers)
        if (peer.readyForBroadcasting)
            host.NetHost!.SendMessageToClient(peer.peerId, bytes, length, message.Mode);
}
else if (gs is NetClientGameService client)
{
    // Host will re-broadcast with overrideSenderId intact
    client.NetClient?.SendMessageToHost(bytes, length, message.Mode);
}
```

### RewardObtainedMessage
```csharp
// Namespace: MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync
struct RewardObtainedMessage : INetMessage
{
    required RewardType rewardType;
    required RunLocation location;
    required bool wasSkipped;
    int? goldAmount;
    CardModel? cardModel;
    RelicModel? relicModel;
    PotionModel? potionModel;

    bool ShouldBroadcast => true;           // host auto-forwards to all clients
    NetTransferMode Mode => NetTransferMode.Reliable;
}
```

### HandleRewardObtainedMessage internals (for reference)
The handler in `RewardSynchronizer` does:
```
Player player = _playerCollection.GetPlayer(senderId);  // senderId = overrideSenderId from packet
switch (message.rewardType)
{
    case RewardType.Gold:
        PlayerCmd.GainGold(message.goldAmount.Value, player);   // local only
```
This is why the senderId in the packet must be the *recipient's* NetId.

---

## Shop UI

**Namespace:** `MegaCrit.Sts2.Core.Nodes.Screens.Shops`

### NMerchantInventory
Patched with `[HarmonyPatch(typeof(NMerchantInventory), "_Ready")]`.

```csharp
// Key child nodes (located via Godot unique-name syntax %)
GetNode<Control>("%SlotsContainer")          // animated container, starts at Y=-1000
GetNodeOrNull<Control>("%MerchantCardRemoval") // card removal slot
GetNode<NMerchantDialogue>("%Dialogue")

// Inventory model
MerchantInventory inv = __instance.Inventory;
Player player = inv?.Player;
```

### Adding a button next to the card-removal slot
```csharp
// 1. Find the anchor node
var cardRemoval = __instance.GetNodeOrNull("%MerchantCardRemoval") as Control;

// 2. Add button as sibling so it animates with SlotsContainer
var parent = cardRemoval.GetParent<Control>();
parent.AddChild(button);

// 3. Defer position update one frame so Size is populated
Action oneShot = null!;
oneShot = () =>
{
    button.Position = cardRemoval.Position + new Vector2(cardRemoval.Size.X + 8, 0);
    __instance.GetTree().ProcessFrame -= oneShot;
};
__instance.GetTree().ProcessFrame += oneShot;
```

> **Gotcha:** `%SlotsContainer` tweens from Y=-1000 to Y=0 on open. Always use **local** `Position` (relative to parent), never `GlobalPosition`, so the button animates with it.

---

## Platform / Steam

**Namespace:** `MegaCrit.Sts2.Core.Platform`

```csharp
// Get a player's Steam display name
INetGameService gs = ...;
string name = PlatformUtil.GetPlayerName(gs.Platform, player.NetId);

// Get local player's platform ID
ulong myId = PlatformUtil.GetLocalPlayerId(gs.Platform);

// Show Steam invite dialog
PlatformUtil.OpenInviteDialog(gs);
```

**PlatformType:** `None` | `Steam`

---

## Logging

**Namespace:** `MegaCrit.Sts2.Core.Logging`

```csharp
Log.Debug("[my-mod] value = " + x);
Log.Info("[my-mod] player entered shop");
Log.Warn("[my-mod] unexpected state");
Log.Error("[my-mod] failed: " + ex.Message);

// Or create a scoped logger (recommended for libraries)
var logger = new Logger("MyMod", LogType.Generic);
logger.Info("loaded");
```

`GD.Print()` also works but bypasses the game's log routing.

---

## Card / Relic / Potion Commands

**Namespace:** `MegaCrit.Sts2.Core.Commands`

```csharp
// Cards
await CardPileCmd.RemoveFromDeck(card);
await CardPileCmd.Upgrade(card);
await CardPileCmd.Transform(card, newCard);

// Relics
await RelicCmd.Obtain(relicModel, player);
await RelicCmd.Remove(relicModel);

// Potions
var result = await PotionCmd.TryToProcure(potionModel, player);
if (result.Success) { ... }
```

---

## Godot Helpers

**Namespace:** `MegaCrit.Sts2.Core.Helpers`

```csharp
// Safe async task (logs + rethrows exceptions; use instead of fire-and-forget)
TaskHelper.RunSafely(SomeAsyncMethod());

// Safe node add/remove on main thread
parent.AddChildSafely(child);
parent.RemoveChildSafely(child);
node.QueueFreeSafely();

// Defer to next frame
__instance.GetTree().ProcessFrame += () => { /* runs once next frame */ };
```

---

## Common Caveats

| Situation | Caveat |
|-----------|--------|
| `PlayerCmd.GainGold` | Local only — always follow with `RewardSynchronizer.SyncLocalObtainedGold` in multiplayer |
| `SyncLocalObtainedGold` | Uses local player's `NetId` as `senderId` — wrong player if called for someone else |
| `GlobalPosition` in `_Ready` | `SlotsContainer` is at Y=-1000 during `_Ready`; use `Position` (local) instead |
| Custom `INetMessage` | `[GenerateSubtypes]` is compile-time only; cannot add new message types from mods |
| `HandleRewardObtainedMessage` | Buffers messages during combat; safe to send from shop |
| `RewardSynchronizer._gameService` | Private field — access via `BindingFlags.NonPublic | BindingFlags.Instance` |
