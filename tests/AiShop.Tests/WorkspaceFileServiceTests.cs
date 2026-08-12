using AiShop.Application.Common;
using AiShop.Domain.Sessions;
using AiShop.Infrastructure.Persistence;
using AiShop.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiShop.Tests;

public class WorkspaceFileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aishop-wsfiles-" + Guid.NewGuid().ToString("n"));
    private readonly string _workspace;
    private readonly JsonEntityStore<ChatSession> _sessions;
    private readonly WorkspaceFileService _service;
    private readonly ChatSession _session;

    public WorkspaceFileServiceTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);

        _sessions = new JsonEntityStore<ChatSession>(
            Options.Create(new OrchestratorOptions { DataPath = _root }),
            NullLogger<JsonEntityStore<ChatSession>>.Instance);

        _session = _sessions.UpsertAsync(new ChatSession { Title = "test", WorkingDirectory = _workspace })
            .GetAwaiter().GetResult();

        _service = new WorkspaceFileService(_sessions, NullLogger<WorkspaceFileService>.Instance);
    }

    private string Write(string relativePath, string content = "hello")
    {
        var path = Path.Combine(_workspace, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Lists_what_the_agent_wrote_including_nested_files()
    {
        Write("report.md");
        Write("out/chart.png");

        var files = await _service.ListAsync(_session.Id);

        Assert.Contains(files, f => f.RelativePath == "report.md" && f.IsText);
        Assert.Contains(files, f => f.RelativePath == "out/chart.png" && f.IsImage);
    }

    [Fact]
    public async Task Noise_directories_are_skipped()
    {
        Write("keep.txt");
        Write(Path.Combine(".git", "config"));
        Write(Path.Combine("node_modules", "pkg", "index.js"));

        var files = await _service.ListAsync(_session.Id);

        Assert.Single(files);
        Assert.Equal("keep.txt", files[0].RelativePath);
    }

    [Fact]
    public async Task Opens_a_file_with_a_sensible_content_type()
    {
        Write("out/chart.png", "not really a png");

        var opened = await _service.OpenAsync(_session.Id, "out/chart.png");

        Assert.NotNull(opened);
        await using var content = opened!.Value.Content;
        Assert.Equal("chart.png", opened.Value.FileName);
        Assert.Equal("image/png", opened.Value.ContentType);
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\secrets.json")]
    [InlineData("out/../../escaped.txt")]
    public async Task A_path_cannot_escape_the_workspace(string attempt)
    {
        // Something worth stealing, one level above the workspace.
        File.WriteAllText(Path.Combine(_root, "escaped.txt"), "secret");
        File.WriteAllText(Path.Combine(_root, "secrets.json"), "secret");

        var opened = await _service.OpenAsync(_session.Id, attempt);

        Assert.Null(opened);
    }

    [Fact]
    public async Task An_unknown_session_yields_nothing()
    {
        Assert.Empty(await _service.ListAsync("does-not-exist"));
        Assert.Null(await _service.OpenAsync("does-not-exist", "report.md"));
    }

    [Fact]
    public async Task A_missing_file_yields_nothing()
    {
        Assert.Null(await _service.OpenAsync(_session.Id, "not-there.txt"));
    }

    [Fact]
    public async Task The_newest_file_comes_first()
    {
        Write("old.txt");
        await Task.Delay(1100);
        Write("new.txt");

        var files = await _service.ListAsync(_session.Id);

        Assert.Equal("new.txt", files[0].RelativePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
