using BaseLib.Config;

namespace Test.Scripts;

/// <summary>
/// In-game configurable settings for the Send Gold mod.
/// Registered with BaseLib so players can adjust values from the mod settings menu.
/// </summary>
public class ShopModConfig : SimpleModConfig
{
    /// <summary>
    /// Percentage fee deducted from each gold transfer (0–100).
    /// Recipient receives: floor( amount × (1 - Fee/100) )
    /// </summary>
    [SliderRange(0, 100, 1)]
    public static int Fee { get; set; } = 10;
}
