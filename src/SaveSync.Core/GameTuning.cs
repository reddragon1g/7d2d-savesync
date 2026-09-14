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

    public const string Playable = "playable";

    /// <summary>
    /// What to actually play on: decent looking, and still nowhere near the thermal edge.
    ///
    /// The low-heat profile bought the largest margin available at a time when nobody knew how
    /// much was needed, and it overpaid twice over - the card settled at 21% busy, and the game
    /// rendered at 960x540 on a 1080p screen, which is genuinely unpleasant to look at.
    ///
    /// This spends that margin where it buys the most picture per watt. Texture quality and
    /// filtering come back almost to maximum because they cost memory rather than time, and there
    /// is 3.7 GB of unused video memory sitting there. Render scale goes up by half again. What
    /// stays off is the genuinely expensive per-pixel work - shadows, ambient occlusion,
    /// reflections, sun shafts - which is also mostly what was already off on that machine.
    ///
    /// Deliberately short of 0.75, which was tried and collapsed. The gap between "fine" and
    /// "pinned at 300 MHz" turned out to be narrow, and there is no reason to go looking for it.
    /// </summary>
    public static readonly Setting[] PlayableProfile =
    {
        new("OptionsGfxUpscalerMode", "4", "render below native and upscale"),
        new("OptionsGfxDynamicScale", "0.65", "render at 65% - half again the pixels of the safe profile"),
        new("OptionsGfxVsync", "2", "lock to 30 fps, tear-free, for half the work of 60"),
        new("OptionsGfxLimitFpsInGame", "30", "and a frame cap as well, for if vsync is ever turned off"),

        // Memory, not time - which is exactly the currency to spend when the constraint is heat.
        // Texture quality is a mipmap limit: ApplyTextureQuality assigns it straight to
        // GameRenderManager.TextureMipmapLimit, where 0 means no reduction at all. It costs video
        // memory to hold the full-size textures and nothing whatsoever per frame to draw them, and
        // there were 3.2 GB of the card's 6 sitting unused.
        //
        // (OptionsGfxStreamMipmaps looks like it belongs here and does not: ApplyTextureQuality
        // hard-codes streamingMipmapsActive to true and never reads that preference. Setting it
        // would have changed nothing while looking like it had.)
        new("OptionsGfxTexQuality", "0", "FULL-size textures - pure video memory, free per frame"),
        new("OptionsGfxTexFilter", "3", "maximum anisotropic filtering - bandwidth, not arithmetic"),
        new("OptionsGfxObjQuality", "2", "object detail back to what this PC had"),
        new("OptionsGfxTerrainQuality", "2", "terrain back to what this PC had"),
        new("OptionsGfxTreeDistance", "2", "trees visible again, but not to the horizon"),
        new("OptionsGfxGrassDistance", "1", "some grass"),
        new("OptionsGfxAA", "1", "a little anti-aliasing, which upscaling benefits from"),

        // Lighting. These are back to exactly what this PC had, and they are back because turning
        // them off was a mistake worth recording: a world with no shadows in it does not read as
        // "lower quality", it reads as BROKEN. Everything is evenly lit, nothing sits on the
        // ground, and the reaction is "something is wrong with the game" rather than "the settings
        // are low" - which is precisely what happened.
        //
        // Shadow DISTANCE is what costs; shadow quality at the lowest tier that still draws them
        // is cheap, and it was already this machine's own setting.
        new("OptionsGfxShadowQuality", "1", "shadows ON at their cheapest tier - as this PC had them"),
        new("OptionsGfxShadowDistance", "0", "close-range shadows only, which is where the cost is"),
        new("OptionsGfxOcclusion", "true", "contact shading back, as this PC had it"),
        new("OptionsGfxSSReflections", "1", "screen-space reflections back, as this PC had them"),
        new("OptionsGfxSignQuality", "2", "signs as this PC had them"),

        // Genuinely expensive per-pixel work, and all of it was already off on this machine - so
        // none of this is a downgrade from what the person here was looking at.
        new("OptionsGfxReflectQuality", "0", "no reflection probes - was already off here"),
        new("OptionsGfxReflectShadows", "false", "no shadows in reflections - was already off here"),
        new("OptionsGfxSSAO", "false", "no ambient occlusion - was already off here"),
        new("OptionsGfxSunShafts", "false", "no sun shafts - was already off here"),
        new("OptionsGfxBloom", "false", "no bloom - was already off here"),
        new("OptionsGfxDOF", "false", "no depth of field - was already off here"),
        new("OptionsGfxMotionBlur", "0", "no motion blur - was already off here"),
        new("OptionsGfxWaterQuality", "0", "lowest water"),
        new("OptionsGfxViewDistance", "5", "the shortest view distance the game offers"),
    };

    public const string Steady = "steady";

    /// <summary>
    /// Half scale, with the lighting kept. The profile written after getting it wrong twice.
    ///
    /// Two attempts at a middle ground both failed, in opposite directions. The first turned the
    /// shadows off to buy margin, and a world with no shadows in it reads as broken rather than
    /// cheap - "there's something wrong with the lighting" was the report, and it was right. The
    /// second put the shadows back AND raised textures to maximum on the reasoning that texture
    /// memory is free when the constraint is heat. It is not: full-size mipmaps and sixteen-times
    /// anisotropic filtering cost memory BANDWIDTH and texture fetches, every frame, and on a card
    /// held at 300 MHz that is real work. It went straight back over the cliff.
    ///
    /// So this spends the margin on the thing that was actually missed. Shadows on, at the
    /// cheapest tier that still draws them, which was this PC's own setting. Textures at the value
    /// that was measured holding 30 fps rather than the value that was reasoned about. And the
    /// render scale back to the half that is known - not assumed - to be on the safe side.
    ///
    /// Blurrier than anyone would like. But a soft picture with shadows in it looks like a game;
    /// a sharp one without them looks broken.
    /// </summary>
    public static readonly Setting[] SteadyProfile =
    {
        // The same pixels, reconstructed properly instead of stretched.
        //
        // Mode 4 is a plain resolution scale: render small, blow it up, and it looks exactly as
        // bad as that sounds - "the game looks like shit" was the verdict, and it was fair. Mode 5
        // is DLSS, which renders the same number of pixels and reconstructs the frame from motion
        // vectors and history. The cost is in the internal resolution, and the internal resolution
        // is set by the preset, not by DynamicScale: preset 1 is Performance, which is a half-scale
        // render per axis - the identical pixel count that measured 30 fps at 38% busy and 79C.
        //
        // Better still, this was nearly this PC's own setting already. It was on FSR3 at Balanced
        // before any of this; replacing a real temporal upscaler with a linear stretch was a
        // downgrade nobody asked for.
        //
        // Safe to ask for: GameRenderManager falls straight back to mode 4 if DLSS or FSR3 turn
        // out to be unsupported, which is why DynamicScale stays set to the value that works.
        new("OptionsGfxUpscalerMode", "5", "DLSS - the RTX 2060 has the hardware for it"),
        new("OptionsGfxFSRPreset", "1", "Performance: a half-scale render, reconstructed rather than stretched"),
        new("OptionsGfxDynamicScale", "0.5", "only used if DLSS is unsupported - the known-good fallback"),
        new("OptionsGfxVsync", "2", "lock to 30 fps, tear-free, for half the work of 60"),
        new("OptionsGfxLimitFpsInGame", "30", "and a frame cap as well, for if vsync is ever turned off"),

        // The lighting, back to this PC's own settings. This is what the margin is being spent on.
        new("OptionsGfxShadowQuality", "1", "shadows ON at their cheapest tier - as this PC had them"),
        new("OptionsGfxShadowDistance", "0", "close-range only, which is where the cost of shadows is"),
        new("OptionsGfxOcclusion", "true", "contact shading back, as this PC had it"),
        new("OptionsGfxSSReflections", "1", "screen-space reflections back, as this PC had them"),

        // Measured values, not reasoned ones.
        new("OptionsGfxTexQuality", "1", "textures one step down - the value measured at 30 fps"),
        new("OptionsGfxTexFilter", "2", "filtering one step down, for the same reason"),
        new("OptionsGfxObjQuality", "2", "object detail as this PC had it"),
        new("OptionsGfxTerrainQuality", "2", "terrain as this PC had it"),
        new("OptionsGfxTreeDistance", "2", "trees, but not to the horizon"),
        new("OptionsGfxGrassDistance", "1", "some grass"),
        new("OptionsGfxAA", "1", "a little anti-aliasing, which upscaling benefits from"),
        new("OptionsGfxSignQuality", "2", "signs as this PC had them"),

        // Genuinely expensive, and all already off on this machine.
        new("OptionsGfxReflectQuality", "0", "no reflection probes - was already off here"),
        new("OptionsGfxReflectShadows", "false", "no shadows in reflections - was already off here"),
        new("OptionsGfxSSAO", "false", "no ambient occlusion - was already off here"),
        new("OptionsGfxSunShafts", "false", "no sun shafts - was already off here"),
        new("OptionsGfxBloom", "false", "no bloom - was already off here"),
        new("OptionsGfxDOF", "false", "no depth of field - was already off here"),
        new("OptionsGfxMotionBlur", "0", "no motion blur - was already off here"),
        new("OptionsGfxWaterQuality", "0", "lowest water"),
        new("OptionsGfxViewDistance", "5", "the shortest view distance the game offers"),
    };

    public const string Sharp = "sharp";
    public const string Sharper = "sharper";

    /// <summary>
    /// Steps the upscaler up one quality level, and again.
    ///
    /// UpscalingSetQuality maps the preset to how much is actually rendered: 1 is Performance and
    /// draws at half scale per axis, 2 is Balanced, 3 is Quality at roughly two thirds. Everything
    /// else about the frame stays where it was measured, so these are the one dial to turn when
    /// there is thermal headroom going spare and the complaint is that it looks soft.
    ///
    /// Separate and composable on purpose. Turning one dial at a time is the only reason any of
    /// the earlier findings here are trustworthy, and the gap between comfortable and collapsed on
    /// this machine turned out to be narrow enough to walk into by accident.
    /// </summary>
    public static readonly Setting[] SharpProfile =
    {
        new("OptionsGfxFSRPreset", "2", "Balanced - about a third more pixels than Performance"),
    };

    public static readonly Setting[] SharperProfile =
    {
        new("OptionsGfxFSRPreset", "3", "Quality - roughly two thirds scale, close to native"),
    };

    public const string NoShadow = "noshadow";

    /// <summary>
    /// Turns every shadow off, as a measurement rather than a preference.
    ///
    /// QualitySettings.shadows is global: at quality 0 it is Disable and NOTHING casts a shadow,
    /// at 1 it is HardOnly and every shadow-casting light in range renders a shadow map - which
    /// for a point light is a cubemap, six renders. A base with a great many lights in it pays
    /// that bill once per light, concentrated exactly where the lights were put.
    ///
    /// Composed onto another profile, this isolates what shadows cost in a particular base. It is
    /// not meant to be lived with - a world with no shadows in it reads as broken - but the
    /// difference between running with it and without it is the number that says whether the
    /// lights are the problem.
    /// </summary>
    public static readonly Setting[] NoShadowProfile =
    {
        new("OptionsGfxShadowQuality", "0", "no shadows from anything, including every light"),
    };

    public const string BaseFix = "basefix";

    /// <summary>
    /// For a save with an enormous player base in it.
    ///
    /// Measured rather than assumed: this save loads 1,292 dynamic mesh items, and the machinery
    /// that manages them was caching almost none of them - MaxRegionCache at 1 and MaxItemCache at
    /// 3, both the minimum the game allows. Everything outside that tiny cache is thrown away and
    /// rebuilt as the player moves through their own base.
    ///
    /// That matters more than a frame rate, because dynamic meshes carry COLLISION. Regenerating
    /// them under somebody's feet slower than they can walk is how a player falls through their
    /// own floor and gets pushed back out - which from a chair looks exactly like rubber-banding
    /// and being flung around, and which no frame-rate average will ever show.
    ///
    /// Worth knowing that this is not a weak-machine problem. A machine with an RTX 3070 Ti loads
    /// the same save and its mesh regeneration thread blocks for 3.9 seconds too. The base really
    /// is heavy; the laptop just has less to absorb it with.
    ///
    /// Costs memory, which is the one thing that laptop has spare - 11 GB free of 24.
    /// </summary>
    public static readonly Setting[] BaseFixProfile =
    {
        new("DynamicMeshMaxRegionCache", "3", "keep 3 regions of base meshes, not 1 - the most the game allows"),
        new("DynamicMeshMaxItemCache", "6", "keep 6 item caches, not 3 - again the maximum"),

        // Imposters are simplified stand-ins for distant parts of the base. They were in here to
        // save work, and they are out again on the same reasoning that raised the textures: the
        // constraint on this machine is heat, memory is what it has spare, and paying for detail
        // in memory costs nothing per frame. It was also already this PC's own setting.
        new("DynamicMeshUseImposters", "false", "full detail on the base, held in the memory that is going spare"),
    };

    public const string NoDymesh = "nodymesh";

    /// <summary>
    /// Turns the dynamic mesh system off entirely. The blunt instrument, kept for a real answer.
    ///
    /// If the caches do not fix it, this says whether dynamic mesh is the cause at all - the base
    /// still renders, through the ordinary chunk path, and the whole subsystem stops running. Not
    /// a setting to leave on without deciding to: it changes how damage to player-built structures
    /// behaves. But as a one-run experiment it answers the question outright.
    /// </summary>
    public static readonly Setting[] NoDymeshProfile =
    {
        new("DynamicMeshEnabled", "false", "stop managing the base as dynamic meshes at all"),
    };

    /// <summary>
    /// Profiles by name, combined with "+" - so "lowheat+basefix" is both.
    ///
    /// Composing matters here because the problems turned out to be separate: a graphics card with
    /// no fan and a base big enough to stall a far better machine are different faults, and a
    /// profile that fixed one while leaving the other looked like it had failed entirely.
    /// </summary>
    public static Setting[]? ByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var found = new List<Setting>();

        foreach (var part in name.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var one = Single(part.Trim());
            if (one is null) return null;         // one unknown name spoils the whole request
            found.AddRange(one);
        }

        // Later profiles win, so "lowheat+basefix" applies basefix's values over lowheat's.
        return found.Count == 0 ? null
             : found.GroupBy(s => s.Pref).Select(g => g.Last()).ToArray();
    }

    private static Setting[]? Single(string name)
        => string.Equals(name, LowHeat, StringComparison.OrdinalIgnoreCase) ? LowHeatProfile
         : string.Equals(name, Balanced, StringComparison.OrdinalIgnoreCase) ? BalancedProfile
         : string.Equals(name, Playable, StringComparison.OrdinalIgnoreCase) ? PlayableProfile
         : string.Equals(name, Steady, StringComparison.OrdinalIgnoreCase) ? SteadyProfile
         : string.Equals(name, Sharp, StringComparison.OrdinalIgnoreCase) ? SharpProfile
         : string.Equals(name, Sharper, StringComparison.OrdinalIgnoreCase) ? SharperProfile
         : string.Equals(name, NoShadow, StringComparison.OrdinalIgnoreCase) ? NoShadowProfile
         : string.Equals(name, BaseFix, StringComparison.OrdinalIgnoreCase) ? BaseFixProfile
         : string.Equals(name, NoDymesh, StringComparison.OrdinalIgnoreCase) ? NoDymeshProfile
         : null;

    /// <summary>The preferences a profile touches, for capturing before they are changed.</summary>
    public static IEnumerable<string> PrefNames(Setting[] profile) => profile.Select(s => s.Pref);

    /// <summary>A profile as command-line arguments the game's own parser understands.</summary>
    public static IEnumerable<string> ToArguments(Setting[] profile)
        => profile.Select(s => $"-{s.Pref}={s.Value}");

    public static string Describe(Setting[] profile)
        => $"{profile.Length} settings ("
           + string.Join(", ", profile.Take(3).Select(s => s.Pref.Replace("OptionsGfx", "")))
           + (profile.Length > 3 ? ", ..." : "") + ")";
}
