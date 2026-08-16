using System.Text;
using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Images;
using DevStudio.Infrastructure.Workspaces;

namespace DevStudio.Infrastructure.Providers.OpenAi;

/// <summary>
/// What a tool call produced. Two audiences, because they want different things: the model needs a
/// description it can reason about, while the transcript wants the thing itself — an image the model
/// forgot to mention is still an image the operator should see.
/// </summary>
public sealed record ToolOutcome(string ForModel, string? ForTranscript = null);

/// <summary>
/// The tools a bare model is given so it can actually do the work. The agent CLIs bring their own;
/// an OpenAI-compatible endpoint is just a model, so the orchestrator advertises these, executes
/// them itself and feeds the results back.
/// </summary>
public sealed class WorkspaceTools
{
    /// <summary>Output kept per command, so one runaway command cannot fill the context.</summary>
    private const int MaxOutputChars = 20_000;

    private readonly string _root;
    private readonly PermissionMode _mode;
    private readonly IProcessRunner _runner;
    private readonly IImageGenerationService? _images;
    private readonly string? _sessionId;
    private readonly WorkspacePathPolicy _policy;

    public WorkspaceTools(
        string workspace,
        PermissionMode mode,
        IProcessRunner runner,
        IImageGenerationService? images = null,
        string? sessionId = null,
        WorkspacePathPolicy? policy = null)
    {
        _root = Path.GetFullPath(workspace);
        _mode = mode;
        _runner = runner;
        _images = images;
        _sessionId = sessionId;
        _policy = policy ?? new WorkspacePathPolicy();
    }

    /// <summary>
    /// Which tools this turn is allowed, by permission mode. Read-only modes are given no way to
    /// change anything — running headless there is nobody to approve a write, so offering one and
    /// refusing it later only wastes a turn.
    /// </summary>
    public JsonArray Definitions()
    {
        var tools = new JsonArray
        {
            Tool("read_file", "Read a UTF-8 text file from the workspace.", new JsonObject
            {
                ["path"] = Property("string", "Path relative to the workspace root."),
                ["offset"] = Property("integer", "First line to return, 1-based. Optional."),
                ["limit"] = Property("integer", "How many lines to return. Optional."),
            }, "path"),
            Tool("list_files", "List files and folders, relative to the workspace root.", new JsonObject
            {
                ["path"] = Property("string", "Folder to list. Defaults to the workspace root."),
            }),
        };

        if (CanWrite)
        {
            tools.Add(Tool("write_file", "Create or overwrite a text file in the workspace.", new JsonObject
            {
                ["path"] = Property("string", "Path relative to the workspace root."),
                ["content"] = Property("string", "The complete new contents of the file."),
            }, "path", "content"));
        }

        if (CanRunCommands)
        {
            tools.Add(Tool("run_command", "Run a shell command in the workspace and return its output.", new JsonObject
            {
                ["command"] = Property("string", "The command line to run."),
            }, "command"));
        }

        // Offered whatever the permission mode: drawing a picture changes nothing on the machine.
        // Writing the copy into the workspace is the part that respects the mode, below.
        if (_images is { AnyConfigured: true })
        {
            tools.Add(Tool("generate_image", "Generate an image from a text description and return a link to it.", new JsonObject
            {
                ["prompt"] = Property("string", "What to draw. Detailed prompts work better than short ones."),
                ["width"] = Property("integer", "Pixels wide. Optional, default 1024."),
                ["height"] = Property("integer", "Pixels tall. Optional, default 1024."),
                ["seed"] = Property("integer", "Optional. The same seed and prompt reproduce the same image."),
                ["backend"] = Property("string", $"Optional service to use: {string.Join(", ", _images.Backends.Where(b => b.Check().Configured).Select(b => b.Backend))}."),
            }, "prompt"));
        }

        return tools;
    }

    private bool CanWrite => _mode is PermissionMode.AcceptEdits or PermissionMode.Unrestricted;

    /// <summary>
    /// Only the mode that already means "no prompts at all, and only inside a disposable worktree"
    /// gets a shell. The others would be handing arbitrary execution to a model nobody is watching.
    /// </summary>
    private bool CanRunCommands => _mode == PermissionMode.Unrestricted;

    /// <summary>Runs one tool call and returns what the model should be told about it.</summary>
    public async Task<ToolOutcome> InvokeAsync(string name, string argumentsJson, CancellationToken ct)
    {
        JsonObject arguments;
        try
        {
            arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject
                        ?? new JsonObject();
        }
        catch (Exception ex)
        {
            // Told rather than thrown: a model that produced malformed arguments can correct itself
            // on the next step, where an exception would end the turn.
            return new ToolOutcome($"Error: the arguments were not valid JSON ({ex.Message}).");
        }

        try
        {
            return name switch
            {
                "read_file" => new ToolOutcome(ReadFile(arguments)),
                "list_files" => new ToolOutcome(ListFiles(arguments)),
                "write_file" when CanWrite => new ToolOutcome(WriteFile(arguments)),
                "run_command" when CanRunCommands => new ToolOutcome(await RunCommandAsync(arguments, ct)),
                "generate_image" when _images is not null => await GenerateImageAsync(arguments, ct),
                "write_file" or "run_command" => new ToolOutcome($"Error: {name} is not allowed in {_mode} mode."),
                _ => new ToolOutcome($"Error: there is no tool called '{name}'."),
            };
        }
        catch (Exception ex)
        {
            return new ToolOutcome($"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Draws something, then puts it where both audiences can reach it: a link for the transcript,
    /// and — when the mode allows writing — a copy in the workspace so the agent can go on to use
    /// the file rather than only refer to it.
    /// </summary>
    private async Task<ToolOutcome> GenerateImageAsync(JsonObject arguments, CancellationToken ct)
    {
        var request = new ImageRequest
        {
            Prompt = Text(arguments, "prompt"),
            Width = Number(arguments, "width") ?? 1024,
            Height = Number(arguments, "height") ?? 1024,
            Seed = Number(arguments, "seed"),
        };

        ImageBackend? backend = Enum.TryParse<ImageBackend>(Text(arguments, "backend", required: false), true, out var parsed)
            ? parsed
            : null;

        var image = await _images!.GenerateAsync(request, backend, _sessionId, ct);
        var url = _images.UrlFor(image);

        var saved = string.Empty;

        if (CanWrite)
        {
            var directory = Path.Combine(_root, "generated-images");
            Directory.CreateDirectory(directory);

            var source = Path.Combine(_images.GetImagesPath(), image.FileName);
            File.Copy(source, Path.Combine(directory, image.FileName), overwrite: true);

            saved = $" A copy is in the workspace at generated-images/{image.FileName}.";
        }

        return new ToolOutcome(
            $"Generated a {image.Width}×{image.Height} image with {image.Backend} ({image.Model}), available at {url}.{saved} " +
            $"It has already been shown to the user, so there is no need to repeat the link.",

            // The operator sees the picture whether or not the model chooses to mention it.
            $"![{image.Prompt}]({url})");
    }

    private string ReadFile(JsonObject arguments)
    {
        var path = Resolve(Text(arguments, "path"));
        var lines = File.ReadAllLines(path);

        var offset = Math.Max(0, (Number(arguments, "offset") ?? 1) - 1);
        var limit = Number(arguments, "limit") ?? lines.Length;

        var slice = lines.Skip(offset).Take(Math.Max(0, limit)).ToList();
        return slice.Count == 0 ? "(the file is empty, or the range is past its end)" : string.Join('\n', slice);
    }

    private string ListFiles(JsonObject arguments)
    {
        var path = string.IsNullOrWhiteSpace(Text(arguments, "path", required: false))
            ? _root
            : Resolve(Text(arguments, "path"));

        if (!Directory.Exists(path))
            return "Error: there is no such folder.";

        var entries = Directory.EnumerateFileSystemEntries(path)
            .Select(entry => Directory.Exists(entry)
                ? Path.GetRelativePath(_root, entry).Replace('\\', '/') + "/"
                : Path.GetRelativePath(_root, entry).Replace('\\', '/'))
            .Order()
            .ToList();

        return entries.Count == 0 ? "(empty)" : string.Join('\n', entries);
    }

    private string WriteFile(JsonObject arguments)
    {
        var path = Resolve(Text(arguments, "path"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Text(arguments, "content", required: false) ?? string.Empty);

        return $"Wrote {Path.GetRelativePath(_root, path).Replace('\\', '/')}.";
    }

    private async Task<string> RunCommandAsync(JsonObject arguments, CancellationToken ct)
    {
        var command = Text(arguments, "command");

        var (shell, shellArguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", command })
            : ("/bin/sh", ["-c", command]);

        var result = await _runner.RunAsync(
            new ProcessRequest(shell, shellArguments, _root, TimeoutSeconds: 120),
            ct);

        var output = new StringBuilder();
        output.AppendLine($"exit code: {result.ExitCode}");

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            output.AppendLine(result.StandardOutput.Trim());

        if (!string.IsNullOrWhiteSpace(result.StandardError))
            output.AppendLine(result.StandardError.Trim());

        var text = output.ToString().TrimEnd();
        return text.Length <= MaxOutputChars ? text : text[..MaxOutputChars] + "\n… (output truncated)";
    }

    /// <summary>Keeps every tool inside the session's own workspace.</summary>
    private string Resolve(string path)
    {
        if (!WorkspacePathGuard.TryResolveWithin(
                _root,
                path,
                out var full,
                _policy.ValidatePaths,
                _policy.FollowSymlinks))
            throw new InvalidOperationException("that path is outside the workspace");

        return full;
    }

    private static string Text(JsonObject arguments, string name, bool required = true)
    {
        var value = arguments[name]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(value) && required)
            throw new InvalidOperationException($"'{name}' is required");

        return value ?? string.Empty;
    }

    private static int? Number(JsonObject arguments, string name)
    {
        if (arguments[name] is not { } node)
            return null;

        return node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => node.GetValue<int>(),
            System.Text.Json.JsonValueKind.String => int.TryParse(node.GetValue<string>(), out var parsed) ? parsed : null,
            _ => null,
        };
    }

    private static JsonObject Property(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) =>
        new()
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r!)]),
                },
            },
        };
}
