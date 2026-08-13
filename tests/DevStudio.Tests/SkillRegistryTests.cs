using System.Net;
using System.Text;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Skills;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// Pulling a skill means running someone else's file names through our own file system, so the
/// interesting cases here are the ones where the registry says something we should not simply obey.
/// </summary>
public class SkillRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-skills-" + Guid.NewGuid().ToString("n"));

    private static SkillsShRegistry Registry(HttpResponseMessage response, Action<HttpRequestMessage>? onRequest = null) =>
        new(
            new HttpClient(new StubHandler(response, onRequest)) { BaseAddress = new Uri("https://skills.sh/") },
            NullLogger<SkillsShRegistry>.Instance);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Search_results_carry_what_it_takes_to_choose_between_them()
    {
        var registry = Registry(Json(
            """
            {
              "query": "react",
              "skills": [
                {
                  "id": "vercel-labs/agent-skills/vercel-react-best-practices",
                  "skillId": "vercel-react-best-practices",
                  "name": "vercel-react-best-practices",
                  "installs": 628592,
                  "source": "vercel-labs/agent-skills"
                }
              ]
            }
            """));

        var results = await registry.SearchAsync("react");

        var hit = Assert.Single(results);
        Assert.Equal("vercel-labs/agent-skills", hit.Source);
        Assert.Equal("vercel-react-best-practices", hit.Slug);
        Assert.Equal(628592, hit.Installs);
        Assert.Equal("vercel-labs/agent-skills/vercel-react-best-practices", hit.Id);
    }

    [Fact]
    public async Task An_empty_query_never_reaches_the_registry()
    {
        var called = false;
        var registry = Registry(Json("""{"skills":[]}"""), _ => called = true);

        Assert.Empty(await registry.SearchAsync("   "));
        Assert.False(called);
    }

    [Fact]
    public async Task A_download_is_split_into_the_skill_and_the_files_it_reads()
    {
        var registry = Registry(Json(Download(
            ("SKILL.md", "---\nname: vercel-react-best-practices\ndescription: React rules\ntags: react, performance\n---\n\nRead `rules/memo.md`."),
            ("rules/memo.md", "# Memo"),
            ("README.md", "# Readme"))));

        var package = await registry.FetchAsync("vercel-labs/agent-skills", "vercel-react-best-practices");

        Assert.NotNull(package);
        Assert.Equal("vercel-react-best-practices", package.Name);
        Assert.Equal("React rules", package.Description);
        Assert.Equal(["react", "performance"], package.Tags);

        // The body is what becomes SKILL.md again on the way out, so the frontmatter must be gone.
        Assert.DoesNotContain("description:", package.Content);
        Assert.Contains("Read `rules/memo.md`.", package.Content);

        // SKILL.md is regenerated from the entity, so shipping the original as a supporting file
        // would put a second, stale copy in the workspace.
        Assert.Equal(["rules/memo.md", "README.md"], package.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task A_download_without_a_manifest_is_not_a_skill()
    {
        var registry = Registry(Json(Download(("README.md", "# Nothing to install"))));

        Assert.Null(await registry.FetchAsync("owner/repo", "slug"));
    }

    [Theory]
    [InlineData("../../escape.md")]
    [InlineData("/etc/passwd")]
    [InlineData("rules/../../escape.md")]
    [InlineData("C:/Windows/system32/evil.md")]
    public async Task File_names_that_could_leave_the_skill_folder_are_dropped(string path)
    {
        var registry = Registry(Json(Download(
            ("SKILL.md", "---\nname: s\ndescription: d\n---\n\nBody"),
            (path, "owned"),
            ("rules/fine.md", "kept"))));

        var package = await registry.FetchAsync("owner/repo", "s");

        Assert.NotNull(package);
        Assert.Equal(["rules/fine.md"], package.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task An_unreachable_registry_is_reported_as_no_results_rather_than_a_crash()
    {
        // A response message is single-use, so each call gets its own rather than sharing one.
        Assert.Empty(await Registry(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)).SearchAsync("react"));
        Assert.Null(await Registry(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)).FetchAsync("owner/repo", "slug"));
    }

    [Fact]
    public async Task Importing_stores_the_skill_and_lands_its_files_on_the_volume()
    {
        var importer = Importer(Package(files: [new SkillFile("rules/memo.md", "# Memo")]));

        var result = await importer.ImportAsync("owner/repo", "slug");

        Assert.True(result.Changed);
        Assert.Equal("owner/repo/slug", result.Skill.RegistrySource);
        Assert.Equal("hash-1", result.Skill.RegistryHash);
        Assert.Equal(1, result.Skill.BundleFileCount);
        Assert.Equal("# Memo", await File.ReadAllTextAsync(BundleFile(result.Skill.Id, "rules/memo.md")));
    }

    [Fact]
    public async Task Pulling_the_same_version_again_changes_nothing()
    {
        var store = Store();
        var first = await Importer(Package(), store).ImportAsync("owner/repo", "slug");
        var second = await Importer(Package(), store).ImportAsync("owner/repo", "slug");

        Assert.False(second.Changed);
        Assert.Equal(first.Skill.Id, second.Skill.Id);
        Assert.Single(await store.GetAllAsync());
    }

    [Fact]
    public async Task An_update_removes_files_the_author_dropped()
    {
        var store = Store();
        var first = await Importer(Package(files: [new SkillFile("rules/old.md", "gone soon")]), store)
            .ImportAsync("owner/repo", "slug");

        var updated = await Importer(Package(hash: "hash-2", files: [new SkillFile("rules/new.md", "here")]), store)
            .ImportAsync("owner/repo", "slug");

        Assert.True(updated.Changed);
        Assert.Equal(first.Skill.Id, updated.Skill.Id);

        // A leftover file is worse than a missing one: SKILL.md stops mentioning it, but an agent
        // reading the folder still finds it and treats it as current.
        Assert.False(File.Exists(BundleFile(updated.Skill.Id, "rules/old.md")));
        Assert.True(File.Exists(BundleFile(updated.Skill.Id, "rules/new.md")));
    }

    [Fact]
    public async Task Deleting_a_skill_takes_its_files_with_it()
    {
        var importer = Importer(Package(files: [new SkillFile("rules/memo.md", "# Memo")]));
        var imported = await importer.ImportAsync("owner/repo", "slug");

        await importer.DeleteBundleAsync(imported.Skill.Id);

        Assert.False(File.Exists(BundleFile(imported.Skill.Id, "rules/memo.md")));
    }

    [Fact]
    public async Task A_skill_the_registry_does_not_have_fails_loudly()
    {
        var importer = Importer(package: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync("owner/repo", "missing"));
    }

    private static string Download(params (string Path, string Contents)[] files)
    {
        var entries = files.Select(f =>
            $$"""{"path": {{Quote(f.Path)}}, "contents": {{Quote(f.Contents)}}}""");

        return $$"""{"files": [{{string.Join(",", entries)}}], "hash": "abc123"}""";
    }

    private static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static SkillPackage Package(string hash = "hash-1", IReadOnlyList<SkillFile>? files = null) =>
        new("owner/repo", "slug", "Slug skill", "What it is for", "Body", [], files ?? [], hash);

    private JsonEntityStore<Skill> Store() =>
        new(Options.Create(new OrchestratorOptions { DataPath = _root }), NullLogger<JsonEntityStore<Skill>>.Instance);

    private SkillImporter Importer(SkillPackage? package, JsonEntityStore<Skill>? store = null) =>
        new(
            new StubRegistry(package),
            store ?? Store(),
            Options.Create(new OrchestratorOptions { DataPath = _root }),
            NullLogger<SkillImporter>.Instance);

    private string BundleFile(string skillId, string relative) =>
        Path.Combine(_root, "skill-bundles", skillId, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    private sealed class StubRegistry(SkillPackage? package) : ISkillRegistry
    {
        public string Name => "stub";

        public Task<IReadOnlyList<SkillSearchResult>> SearchAsync(string query, int limit = 20, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SkillSearchResult>>([]);

        public Task<SkillPackage?> FetchAsync(string source, string slug, CancellationToken ct = default) =>
            Task.FromResult(package);
    }

    private sealed class StubHandler(HttpResponseMessage response, Action<HttpRequestMessage>? onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onRequest?.Invoke(request);
            return Task.FromResult(response);
        }
    }
}
