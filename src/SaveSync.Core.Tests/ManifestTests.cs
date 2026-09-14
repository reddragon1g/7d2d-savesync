using SaveSync.Core;
using Xunit;

namespace SaveSync.Core.Tests;

public class ManifestTests
{
    [Fact]
    public void Build_then_verify_is_clean()
    {
        using var env = new TestEnv();
        var save = env.MakeSave();

        var m = Manifest.Build(save);
        Assert.True(m.Count > 10);
        Assert.Empty(m.Verify(save));
    }

    [Fact]
    public void Passport_is_not_part_of_the_payload()
    {
        using var env = new TestEnv();
        var save = env.MakeSave();
        env.AdoptedSlot();

        var m = Manifest.Build(save);
        Assert.DoesNotContain(m.Entries, e => e.Path.Equals(Passport.FileName, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(m.Verify(save));
    }

    [Fact]
    public void Flipped_byte_is_caught()
    {
        using var env = new TestEnv();
        var save = env.MakeSave();
        var m = Manifest.Build(save);

        TestEnv.CorruptFile(Path.Combine(save, "Region", "r.0.0.7rg"));

        var problems = m.Verify(save);
        var problem = Assert.Single(problems);
        Assert.Contains("Region/r.0.0.7rg", problem.Path);
        Assert.Contains("do not match", problem.Problem);
    }

    [Fact]
    public void Truncated_file_is_caught_as_a_size_mismatch()
    {
        using var env = new TestEnv();
        var save = env.MakeSave();
        var m = Manifest.Build(save);

        var target = Path.Combine(save, "main.ttw");
        File.WriteAllBytes(target, File.ReadAllBytes(target)[..100]);

        var problem = Assert.Single(m.Verify(save));
        Assert.Contains("wrong size", problem.Problem);
    }

    [Fact]
    public void Missing_file_is_caught()
    {
        using var env = new TestEnv();
        var save = env.MakeSave();
        var m = Manifest.Build(save);

        File.Delete(Path.Combine(save, "Player", "EOS_aaaaaaaa111122223333444455556666.ttp"));

        var problem = Assert.Single(m.Verify(save));
        Assert.Equal("missing", problem.Problem);
    }

    [Fact]
    public void Unexpected_extra_file_is_caught()
    {
        using var env = new TestEnv();
        var save = env.MakeSave();
        var m = Manifest.Build(save);

        TestEnv.WriteText(Path.Combine(save, "Region", "stray.7rg"), "left over from a half finished write");

        var problem = Assert.Single(m.Verify(save));
        Assert.Contains("unexpected extra file", problem.Problem);
    }

    [Fact]
    public void ComputeSha_is_stable_across_rebuilds_and_changes_with_content()
    {
        using var env = new TestEnv();
        var save = env.MakeSave();

        var first = Manifest.Build(save).ComputeSha();
        var second = Manifest.Build(save).ComputeSha();
        Assert.Equal(first, second);

        env.Play(save, seed: 7);
        Assert.NotEqual(first, Manifest.Build(save).ComputeSha());
    }

    [Fact]
    public void Diff_reports_only_what_actually_changed()
    {
        using var env = new TestEnv();
        var save = env.MakeSave();
        var before = Manifest.Build(save);

        env.Play(save, seed: 3);
        var after = Manifest.Build(save);

        var need = Manifest.Diff(after, before);

        Assert.NotEmpty(need);
        Assert.True(need.Count < after.Count,
            "delta should be smaller than the whole save, otherwise LAN transfer gains nothing");
        Assert.Contains(need, e => e.Path.EndsWith("main.ttw", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(need, e => e.Path.EndsWith("decoration.7dt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Diff_is_empty_for_identical_trees()
    {
        using var env = new TestEnv();
        var save = env.MakeSave();
        var m = Manifest.Build(save);
        Assert.Empty(Manifest.Diff(m, Manifest.Build(save)));
    }

    [Fact]
    public void Paths_are_forward_slashed_so_packages_are_portable()
    {
        using var env = new TestEnv();
        var save = env.MakeSave();
        var m = Manifest.Build(save);

        Assert.Contains(m.Entries, e => e.Path.Contains('/'));
        Assert.DoesNotContain(m.Entries, e => e.Path.Contains('\\'));
    }

    [Fact]
    public void CopyTree_preserves_content_and_timestamps()
    {
        using var env = new TestEnv();
        var save = env.MakeSave();
        var m = Manifest.Build(save);

        var copy = Path.Combine(env.Root, "copy");
        FileOps.CopyTree(save, copy);

        Assert.Empty(m.Verify(copy));
        Assert.Equal(
            new FileInfo(Path.Combine(save, "main.ttw")).LastWriteTimeUtc,
            new FileInfo(Path.Combine(copy, "main.ttw")).LastWriteTimeUtc);
    }

    [Fact]
    public void CopyTree_refuses_to_copy_into_itself()
    {
        using var env = new TestEnv();
        var save = env.MakeSave();
        Assert.Throws<IOException>(() => FileOps.CopyTree(save, Path.Combine(save, "inner")));
    }
}
