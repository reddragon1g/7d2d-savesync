using SaveSync.Core;
using Xunit;

namespace SaveSync.Core.Tests;

/// <summary>
/// Starting the game on a PC nobody is sitting at.
///
/// Nothing here launches anything. What is worth pinning down is the decision made before the
/// launch: which executable, which flags are carried over from that machine's own setup, and
/// whether a save that is not there is refused instead of quietly created.
/// </summary>
public class GameLauncherTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "savesync-tests", "launch-" + Guid.NewGuid().ToString("N"));

    private readonly string _install;

    public GameLauncherTests()
    {
        _install = Path.Combine(_dir, "install");
        Directory.CreateDirectory(Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(_install);

        // Enough of an install to be recognised as one.
        File.WriteAllText(Path.Combine(_install, "7DaysToDie.exe"), "");
        File.WriteAllText(Path.Combine(_install, "7DaysToDie_EAC.exe"), "");
    }

    public void Dispose()
    {
        try { PathUtil.DeleteTree(_dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private GameLocation Location() => new()
    {
        UserDataRoot = _dir,
        InstallDir = _install,
        Provenance = "test",
    };

    private void WriteSave(string world, string name)
    {
        var dir = Path.Combine(_dir, "Saves", world, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.ttw"), "not really a save");
    }

    private void WriteLauncherSettings(bool useEac, string renderer = "dx11", string extra = "")
    {
        File.WriteAllText(Path.Combine(_dir, GameLauncher.LauncherSettingsFile), $$"""
        {
          "ShowLauncher" : false,
          "SettingsVersion" : 1,
          "DefaultRunConfig" : {
            "GraphicsJobs" : true,
            "ExclusiveMode" : false,
            "Renderer" : "{{renderer}}",
            "UseEAC" : {{(useEac ? "true" : "false")}},
            "UseNativeInput" : true,
            "AdditionalParameters" : "{{extra}}"
          }
        }
        """);
    }

    private void WriteLauncherLog(string exe, params string[] args)
    {
        var lines = new List<string> { "", "Orig CWD:  somewhere", "", "Executing: " + exe };
        lines.AddRange(args.Select(a => "    " + a));
        lines.Add("");
        lines.Add("Launched the game executable");

        File.WriteAllLines(Path.Combine(_dir, "logs", GameLauncher.LauncherLogFile), lines);
    }

    // ---------------------------------------------------------------- naming the save

    /// <summary>
    /// The three arguments the game actually needs, in the form it actually accepts.
    ///
    /// -LoadSaveGame= is a bool - LaunchPrefs parses it with bool.TryParse - and putting the save
    /// name in it produces "Could not parse config value" and a game sitting at its menu. The save
    /// is named by -world= and -name=. This test exists because that was got wrong once.
    /// </summary>
    [Fact]
    public void A_save_is_named_by_world_and_name_and_switched_on_by_a_bool()
    {
        WriteSave("Navezgane", "Chris Main Save");
        WriteLauncherLog(Path.Combine(_install, "7DaysToDie.exe"), "-force-d3d11", "-noeac");

        var plan = GameLauncher.PlanLaunch(Location(), _install, "Navezgane", "Chris Main Save");

        Assert.Contains("-world=Navezgane", plan.Arguments);
        Assert.Contains("-name=Chris Main Save", plan.Arguments);
        Assert.Contains("-LoadSaveGame=true", plan.Arguments);

        // Never the name, which is the mistake being guarded against.
        Assert.DoesNotContain("-LoadSaveGame=Chris Main Save", plan.Arguments);
    }

    /// <summary>Asked for nothing in particular, it starts the game and leaves it at its menu.</summary>
    [Fact]
    public void With_no_save_asked_for_nothing_about_saves_is_passed()
    {
        WriteLauncherLog(Path.Combine(_install, "7DaysToDie.exe"), "-force-d3d11", "-noeac");

        var plan = GameLauncher.PlanLaunch(Location(), _install, null, null);

        Assert.DoesNotContain(plan.Arguments, a => a.StartsWith("-LoadSaveGame=", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Arguments, a => a.StartsWith("-name=", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Arguments, a => a.StartsWith("-world=", StringComparison.Ordinal));
    }

    /// <summary>
    /// A save name with spaces in it survives as ONE argument.
    ///
    /// "Chris Main Save" is a real save name on a real machine. Joined into a command line by hand
    /// it becomes -name=Chris plus two strays, and the game would not find that save - it would
    /// make a new empty world called "Chris" instead.
    /// </summary>
    [Fact]
    public void A_save_name_with_spaces_stays_one_argument()
    {
        WriteSave("Navezgane", "Chris Main Save");
        WriteLauncherLog(Path.Combine(_install, "7DaysToDie.exe"));

        var plan = GameLauncher.PlanLaunch(Location(), _install, "Navezgane", "Chris Main Save");

        Assert.Single(plan.Arguments, a => a.StartsWith("-name=", StringComparison.Ordinal));
        Assert.Equal("-name=Chris Main Save",
                     plan.Arguments.First(a => a.StartsWith("-name=", StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------- not inventing a launch

    /// <summary>
    /// EasyAntiCheat is off on these machines because somebody turned it off. Starting the game
    /// for them must not quietly turn it back on.
    /// </summary>
    [Fact]
    public void The_machines_own_launch_is_repeated_including_EAC_being_off()
    {
        WriteLauncherSettings(useEac: false);
        WriteLauncherLog(Path.Combine(_install, "7DaysToDie.exe"), "-force-d3d11", "-nogs", "-noeac");

        var plan = GameLauncher.PlanLaunch(Location(), _install, null, null);

        Assert.Contains("-noeac", plan.Arguments);
        Assert.Equal(Path.Combine(_install, "7DaysToDie.exe"), plan.Exe);
    }

    /// <summary>And the reverse: a PC that wants EAC gets the executable that has it.</summary>
    [Fact]
    public void A_PC_that_uses_EAC_gets_the_EAC_executable()
    {
        WriteLauncherSettings(useEac: true);       // no launcher.log, so this is derived

        var (exe, args) = GameLauncher.DeriveInvocation(_install, GameLauncher.ReadSettings(Location()));

        Assert.Equal(Path.Combine(_install, "7DaysToDie_EAC.exe"), exe);
        Assert.DoesNotContain("-noeac", args);
    }

    /// <summary>Anything typed into the launcher's extra-parameters box comes along too.</summary>
    [Fact]
    public void Extra_parameters_from_the_launcher_are_carried_over()
    {
        WriteLauncherSettings(useEac: false, extra: "-skipintro -somethingelse");

        var (_, args) = GameLauncher.DeriveInvocation(_install, GameLauncher.ReadSettings(Location()));

        Assert.Contains("-skipintro", args);
        Assert.Contains("-somethingelse", args);
    }

    [Fact]
    public void The_renderer_choice_is_honoured()
    {
        WriteLauncherSettings(useEac: false, renderer: "vulkan");

        var (_, args) = GameLauncher.DeriveInvocation(_install, GameLauncher.ReadSettings(Location()));

        Assert.Contains("-force-vulkan", args);
        Assert.DoesNotContain("-force-d3d11", args);
    }

    /// <summary>
    /// The previous launch's log file is not reused.
    ///
    /// Appending to the old one would leave the machine report reading a stale frame rate and
    /// calling it the current session.
    /// </summary>
    [Fact]
    public void The_previous_launchs_logfile_is_replaced_not_repeated()
    {
        WriteLauncherLog(Path.Combine(_install, "7DaysToDie.exe"),
                         "-force-d3d11", "-logfile", @"C:\old\output_log_client__yesterday.txt");

        var plan = GameLauncher.PlanLaunch(Location(), _install, null, null);

        Assert.Single(plan.Arguments, a => a == "-logfile");
        Assert.DoesNotContain(@"C:\old\output_log_client__yesterday.txt", plan.Arguments);

        // And where it does go must still be somewhere the machine report will find it.
        Assert.Equal(Path.Combine(_dir, "logs"), Path.GetDirectoryName(plan.LogFile));
        Assert.StartsWith("output_log", Path.GetFileName(plan.LogFile));
    }

    /// <summary>A save asked for twice in a row does not accumulate arguments.</summary>
    [Fact]
    public void Save_arguments_from_a_previous_launch_are_not_passed_twice()
    {
        WriteSave("Navezgane", "Second");
        WriteLauncherLog(Path.Combine(_install, "7DaysToDie.exe"),
                         "-world=Navezgane", "-name=First", "-LoadSaveGame=true");

        var plan = GameLauncher.PlanLaunch(Location(), _install, "Navezgane", "Second");

        Assert.Single(plan.Arguments, a => a.StartsWith("-name=", StringComparison.Ordinal));
        Assert.Contains("-name=Second", plan.Arguments);
        Assert.DoesNotContain("-name=First", plan.Arguments);
    }

    // ---------------------------------------------------------------- the guard

    /// <summary>
    /// The one outcome worth refusing.
    ///
    /// A name that matches nothing is not an error to the game: "[LoadSaveGame] Creating new save
    /// game '...' from the world '...'". A typo would therefore leave a stranger's PC sitting in a
    /// brand new empty world, looking for all the world like the save had been wiped.
    /// </summary>
    [Fact]
    public void A_save_that_is_not_there_is_refused_rather_than_created()
    {
        WriteSave("Navezgane", "Chris Main Save");

        var why = GameLauncher.WhyCannotLoad(Location(), "Navezgane", "Chirs Main Save");

        Assert.NotNull(why);
        Assert.Contains("no save called", why);
    }

    [Fact]
    public void A_save_that_is_there_is_allowed()
    {
        WriteSave("Navezgane", "Chris Main Save");
        Assert.Null(GameLauncher.WhyCannotLoad(Location(), "Navezgane", "Chris Main Save"));
    }

    /// <summary>The right name in the wrong world is still the wrong save.</summary>
    [Fact]
    public void The_world_has_to_match_too()
    {
        WriteSave("Navezgane", "Chris Main Save");
        Assert.NotNull(GameLauncher.WhyCannotLoad(Location(), "Pregen08k01", "Chris Main Save"));
    }

    /// <summary>A folder with no main.ttw is not a save the game can open.</summary>
    [Fact]
    public void A_save_folder_with_no_world_file_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Saves", "Navezgane", "Empty Shell"));

        var why = GameLauncher.WhyCannotLoad(Location(), "Navezgane", "Empty Shell");

        Assert.NotNull(why);
        Assert.Contains("main.ttw", why);
    }

    // ---------------------------------------------------------------- reading it back

    /// <summary>
    /// What the game is in, read out of its own log.
    ///
    /// The game dumps every preference each time it starts a world, so this needs nothing near the
    /// running game - which matters, because the alternative is asking somebody to look.
    /// </summary>
    [Fact]
    public void The_loaded_save_is_read_back_out_of_the_game_log()
    {
        File.WriteAllLines(Path.Combine(_dir, "logs", "output_log_client__2026-09-14__02-52-00.txt"), new[]
        {
            "2026-09-14T02:52:05 0.110 INF Command line arguments: ...",
            "2026-09-14T02:52:31 26.0 INF [LoadSaveGame] Loading existing save game 'Chris Main Save' (world 'Navezgane').",
            "2026-09-14T02:52:40 35.0 INF StartGame",
            "GamePref.GameMode = GameModeSurvival",
            "GamePref.GameName = Chris Main Save",
            "GamePref.GameWorld = Navezgane",
        });

        var loaded = GameLauncher.ReadLoadedSave(Location());

        Assert.NotNull(loaded);
        Assert.Equal("Chris Main Save", loaded!.SaveName);
        Assert.Equal("Navezgane", loaded.World);
        Assert.Contains("Loading existing save game", loaded.LoadNote);
    }

    /// <summary>A log with nothing loaded says so rather than inventing a save.</summary>
    [Fact]
    public void A_game_that_loaded_nothing_reports_nothing()
    {
        File.WriteAllLines(Path.Combine(_dir, "logs", "output_log_client__2026-09-14__03-00-00.txt"), new[]
        {
            "2026-09-14T03:00:05 0.110 INF Command line arguments: ...",
            "2026-09-14T03:00:09 4.0 INF Started thread ...",
        });

        Assert.Null(GameLauncher.ReadLoadedSave(Location()));
    }

    // ---------------------------------------------------------------- odds and ends

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("-a -b", 2)]
    [InlineData("-UserDataFolder=\"C:\\Some Folder\\Here\"", 1)]
    public void Extra_parameters_split_the_way_a_command_line_would(string text, int expected)
        => Assert.Equal(expected, GameLauncher.SplitArguments(text).Count);

    [Fact]
    public void A_quoted_path_in_the_extra_parameters_stays_whole()
    {
        var parts = GameLauncher.SplitArguments("-UserDataFolder=\"C:\\Some Folder\\Here\" -noeac");

        Assert.Equal(2, parts.Count);
        Assert.Equal(@"-UserDataFolder=C:\Some Folder\Here", parts[0]);
        Assert.Equal("-noeac", parts[1]);
    }

    /// <summary>A PC where the game has never been started has no log to copy, and says so.</summary>
    [Fact]
    public void With_no_launcher_log_the_launch_is_built_from_the_settings_file()
    {
        WriteLauncherSettings(useEac: false);

        Assert.Null(GameLauncher.LastLauncherInvocation(Location()));

        var plan = GameLauncher.PlanLaunch(Location(), _install, null, null);
        Assert.Contains("launchersettings.json", plan.Basis);
        Assert.Contains("-noeac", plan.Arguments);
    }

    /// <summary>A launcher log naming an executable that is gone is not trusted.</summary>
    [Fact]
    public void A_launcher_log_pointing_at_a_missing_executable_is_ignored()
    {
        WriteLauncherLog(@"C:\uninstalled\7DaysToDie.exe", "-force-d3d11");
        Assert.Null(GameLauncher.LastLauncherInvocation(Location()));
    }
}
