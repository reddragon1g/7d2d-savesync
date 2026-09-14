namespace SaveSync.Core;

/// <summary>
/// Settings for a machine that cannot cool itself.
///
/// Written for a real laptop whose graphics card fan has failed. The card equilibrates at 83C and
/// clocks itself down to 300 MHz of a possible 2100 - fourteen per cent - and stays there. Frame
/// rate in that state went 45.8, 26.5, 21.2, 16.5 ... down to a flat 8.5 over fifteen minutes with
/// the player standing still in one place.
///
/// That changes what "turn the settings down" is for. Normally it is about asking less of a card
/// that is too slow. Here the card is not too slow - it is being held at a seventh of its speed,
/// and no setting will raise that ceiling. What settings CAN do is two things:
///
///   1. Make each frame cheap enough to be worth having at 300 MHz. Render scale dominates
///      everything else here: half scale is a quarter of the pixels.
///   2. Stop it burning itself down in the first place. Uncapped, it renders 45 fps flat out for
///      the first thirty seconds, which is precisely the heat that causes the collapse. A cap it
///      can sustain may hold steady where an uncapped one cannot.
///
/// Every value below is the game's own, read out of GameOptionsManager.QualityPresets at index 0 -
/// its "Lowest" preset - rather than invented. That matters more than it sounds: TexQuality runs
/// BACKWARDS, where 3 is the lowest quality and 0 the highest, and a profile built on the obvious
/// assumption would have quietly turned the textures up.
/// </summary>
public static class GameTuning
{
    public sealed record Setting(string Pref, string Value, string Why);

    public const string LowHeat = "lowheat";

    /// <summary>
    /// The game's "Lowest" preset, plus half render scale and a frame cap.
    ///
    /// Applied on the command line rather than written to the registry, because a preference that
    /// has never been set has no registry value to write - Unity names them with a hash that is not
    /// derivable. The command line reaches the same place through the game's own parser.
    /// </summary>
    public static readonly Setting[] LowHeatProfile =
    {
        // The one that actually matters. UpscalerMode 4 is "Scale" - a fixed render scale, with
        // the result upscaled to the display. 0.5 is what the game itself uses for its console
        // performance preset, and it is a quarter of the pixels.
        new("OptionsGfxUpscalerMode", "4", "render below native and upscale"),
        new("OptionsGfxDynamicScale", "0.5", "render at half scale - a quarter of the pixels"),

        // Do not let it sprint into the heat that causes the collapse.
        //
        // Vsync, not the frame limiter. This preference is handed straight to Unity's
        // QualitySettings.vSyncCount, where 2 means "one frame every second refresh" - a hard,
        // tear-free 30 on a 60 Hz panel, for half the work of 60. The frame limiter cannot do this
        // job at all: Unity ignores a frame-rate target whenever vsync is on, which is why asking
        // for 30 the obvious way produced a flat 60 and no complaint from anything.
        new("OptionsGfxVsync", "2", "lock to 30 fps, tear-free, for half the work of 60"),
        new("OptionsGfxLimitFpsInGame", "30", "and a frame cap as well, for if vsync is ever turned off"),

        // The game's own Lowest preset, verbatim.
        new("OptionsGfxAA", "0", "no anti-aliasing"),
        new("OptionsGfxMotionBlur", "0", "no motion blur"),
        new("OptionsGfxTexQuality", "3", "lowest textures - this setting runs backwards, 3 is lowest"),
        new("OptionsGfxTexFilter", "0", "cheapest texture filtering"),
        new("OptionsGfxReflectQuality", "0", "no reflection quality"),
        new("OptionsGfxReflectShadows", "false", "no shadows in reflections"),
        new("OptionsGfxShadowQuality", "0", "lowest shadows"),
        new("OptionsGfxShadowDistance", "0", "shortest shadow distance"),
        new("OptionsGfxLODDistance", "0", "swap to simpler models as early as possible"),
        new("OptionsGfxTerrainQuality", "0", "lowest terrain"),
        new("OptionsGfxObjQuality", "0", "lowest object detail"),
        new("OptionsGfxGrassDistance", "0", "no grass in the distance"),
        new("OptionsGfxTreeDistance", "0", "no trees in the distance"),
        new("OptionsGfxBloom", "false", "no bloom"),
        new("OptionsGfxDOF", "false", "no depth of field"),
        new("OptionsGfxSSAO", "false", "no ambient occlusion"),
        new("OptionsGfxSSReflections", "0", "no screen-space reflections"),
        new("OptionsGfxSunShafts", "false", "no sun shafts"),
        new("OptionsGfxWaterQuality", "0", "lowest water"),
        new("OptionsGfxSignQuality", "0", "lowest sign detail"),
        new("OptionsGfxOcclusion", "false", "no occlusion culling pass"),
        new("OptionsGfxViewDistance", "5", "the shortest view distance the game offers"),
    };

    public const string Balanced = "balanced";

    /// <summary>
    /// The far smaller change that may be all this ever needed.
    ///
    /// The low-heat profile bought the biggest margin available before anyone knew how much was
    /// needed, and it turned out to be far too much: the card settled at 38% busy and COOLING, at
    /// the monitor's refresh ceiling. That is 60% of a graphics card being thrown away along with
    /// every texture, tree and shadow in the game.
    ///
    /// So this touches the render scale and the frame rate, and nothing else. Every other setting
    /// stays exactly as the person on that PC set it - which on that laptop was already close to
    /// minimum anyway, and was never the problem.
    ///
    /// Three-quarter scale alone was tried and failed badly: it went straight back to 7 fps, 98%
    /// busy, 300 MHz. That is not a slope, it is a CLIFF - one side of the thermal budget the card
    /// holds 540 MHz and the monitor's refresh rate, the other side it collapses to its floor and
    /// stays there. Which is exactly what "smooth for a second, then terrible, then smooth" feels
    /// like from a chair.
    ///
    /// So the frame rate is halved at the same time. Thirty locked frames at three-quarter scale
    /// is close to the same work as sixty at half scale - which was measured at 38% busy and
    /// COOLING - but every frame carries over twice the pixels.
    /// </summary>
    public static readonly Setting[] BalancedProfile =
    {
        new("OptionsGfxUpscalerMode", "4", "render below native and upscale"),
        new("OptionsGfxDynamicScale", "0.75", "render at three-quarter scale - about half the pixels"),
        new("OptionsGfxVsync", "2", "lock to 30 fps, tear-free, for half the work of 60"),
        new("OptionsGfxLimitFpsInGame", "30", "and a frame cap as well, for if vsync is ever turned off"),
    };

    /// <summary>The profile by name, or null when the name is not one we know.</summary>
    public static Setting[]? ByName(string? name)
        => string.Equals(name, LowHeat, StringComparison.OrdinalIgnoreCase) ? LowHeatProfile
         : string.Equals(name, Balanced, StringComparison.OrdinalIgnoreCase) ? BalancedProfile
         : null;

    /// <summary>The preferences a profile touches, for capturing before they are changed.</summary>
    public static IEnumerable<string> PrefNames(Setting[] profile) => profile.Select(s => s.Pref);

    /// <summary>A profile as command-line arguments the game's own parser understands.</summary>
    public static IEnumerable<string> ToArguments(Setting[] profile)
        => profile.Select(s => $"-{s.Pref}={s.Value}");

    public static string Describe(Setting[] profile)
        => profile.Length <= 5
            ? $"{profile.Length} settings - render scale and a 30 fps lock, everything else left as this PC has it"
            : $"{profile.Length} settings, the game's own lowest preset plus half render scale "
              + "and a 30 fps cap";
}
