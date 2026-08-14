using DevStudio.Desktop;

namespace DevStudio.Tests;

/// <summary>
/// The web view profile outlives the build that filled it, and the server answers on the same
/// loopback origin every time, so a cached page from an old build is still a valid cache entry for
/// the new one. These are the rules that stop it being served.
/// </summary>
public class WebViewProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-webview-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Each_version_gets_its_own_profile()
    {
        Assert.NotEqual(
            WebViewProfiles.DirectoryFor(_root, "1.0.43"),
            WebViewProfiles.DirectoryFor(_root, "1.0.44"));
    }

    [Fact]
    public void A_version_cannot_name_a_directory_outside_the_root()
    {
        var profile = WebViewProfiles.DirectoryFor(_root, "../../elsewhere");

        Assert.Equal(_root, Path.GetDirectoryName(profile));
    }

    [Fact]
    public void The_profiles_of_replaced_versions_are_removed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "1.0.40"));
        Directory.CreateDirectory(Path.Combine(_root, "1.0.43"));

        var removed = WebViewProfiles.Prune(_root, "1.0.43");

        Assert.Equal(["1.0.40"], removed);
        Assert.True(Directory.Exists(Path.Combine(_root, "1.0.43")));
    }

    [Fact]
    public void The_unversioned_profile_this_replaced_is_removed_on_the_first_launch()
    {
        // What every install before this shipped: one profile directly in the root, carrying cache
        // entries for http://127.0.0.1:7080 written by whatever build was installed at the time.
        Directory.CreateDirectory(Path.Combine(_root, "EBWebView", "Default"));
        File.WriteAllText(Path.Combine(_root, "EBWebView", "Default", "Cache"), "an old page");

        WebViewProfiles.Prune(_root, "1.0.43");

        Assert.False(Directory.Exists(Path.Combine(_root, "EBWebView")));
    }

    [Fact]
    public void A_profile_this_build_has_never_used_is_cleared()
    {
        var profile = WebViewProfiles.DirectoryFor(_root, "development");
        Directory.CreateDirectory(profile);

        Assert.True(WebViewProfiles.CacheIsStale(profile, "development-20260814123300"));
    }

    [Fact]
    public void A_profile_this_build_has_already_cleared_is_left_alone()
    {
        var profile = WebViewProfiles.DirectoryFor(_root, "development");
        WebViewProfiles.RecordCache(profile, "development-20260814123300");

        Assert.False(WebViewProfiles.CacheIsStale(profile, "development-20260814123300"));

        // A rebuild moves the stamp even though the version — and so the directory — has not moved.
        Assert.True(WebViewProfiles.CacheIsStale(profile, "development-20260814170000"));
    }

    [Fact]
    public void A_version_that_sanitises_away_to_nothing_still_names_a_directory()
    {
        Assert.Equal(Path.Combine(_root, "unknown"), WebViewProfiles.DirectoryFor(_root, ".."));
    }

    [Fact]
    public void A_build_stamp_never_disagrees_with_the_version_it_belongs_to()
    {
        // A stamped build is its own stamp, so reinstalling the same release clears nothing. Only a
        // build with no version of its own carries something more specific.
        Assert.StartsWith(DesktopVersion.Current, DesktopVersion.BuildStamp, StringComparison.Ordinal);
    }
}
