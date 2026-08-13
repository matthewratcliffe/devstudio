using System.Text.Json;
using System.Text.Json.Serialization;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Scheduling;

namespace DevStudio.Application.Teams;

/// <summary>
/// The on-disk shape of a team settings repository, and how to read it.
///
/// These are deliberately not the domain entities. A file written by a person refers to an agent as
/// "Claude builder" and a skill as "conventional-commits", not by a GUID this install happens to have
/// assigned; and it should not carry ids, timestamps or run history at all. The importer resolves the
/// names and leaves everything else alone.
/// </summary>
public static class TeamDefinitions
{
    /// <summary>Folder names inside the settings folder, one per kind of definition.</summary>
    public const string AgentsFolder = "agents";
    public const string SkillsFolder = "skills";
    public const string WorkflowsFolder = "workflows";
    public const string SchedulesFolder = "schedules";

    /// <summary>Standing instructions for everyone. Markdown, because a person writes it.</summary>
    public const string StandardsFile = "standards.md";

    /// <summary>
    /// Lenient on purpose: a definition file is hand-written, so a comment, a trailing comma or a
    /// property in the wrong case should not be the difference between a working sync and an error.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>
    /// Splits `---` frontmatter from the body of a markdown file. Unterminated or absent frontmatter
    /// leaves the whole file as the body, which is what a plain markdown document should do.
    /// </summary>
    public static (IReadOnlyDictionary<string, string> Front, string Body) ReadFrontmatter(string text)
    {
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalised = text.Replace("\r\n", "\n");

        if (!normalised.StartsWith("---\n", StringComparison.Ordinal))
            return (empty, normalised.Trim());

        var end = normalised.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
            return (empty, normalised.Trim());

        foreach (var line in normalised[4..end].Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"');

            if (key.Length > 0)
                empty[key] = value;
        }

        // Past the closing marker and its newline.
        var bodyStart = normalised.IndexOf('\n', end + 1);
        return (empty, bodyStart < 0 ? string.Empty : normalised[(bodyStart + 1)..].Trim());
    }

    /// <summary>Splits a frontmatter list written either as `a, b` or as `[a, b]`.</summary>
    public static List<string> ReadList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Trim().Trim('[', ']')
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim('"', '\''))];
}

/// <summary>An agent as written in the repository. Skills are named, not referenced by id.</summary>
public sealed class TeamAgentFile
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AiProvider Provider { get; set; } = AiProvider.Claude;

    /// <summary>Name of a CLI provider defined on this install, for <see cref="AiProvider.Custom"/>.</summary>
    public string? Cli { get; set; }

    public string? Model { get; set; }
    public string? Effort { get; set; }

    /// <summary>Model for the opening turns, before the handover to <see cref="Model"/>.</summary>
    public string? OpeningModel { get; set; }

    public string? OpeningEffort { get; set; }
    public int OpeningTurns { get; set; }

    public PermissionMode PermissionMode { get; set; } = PermissionMode.AcceptEdits;
    public string SystemPrompt { get; set; } = string.Empty;
    public string DefaultPrompt { get; set; } = string.Empty;
    public bool UseWorktree { get; set; } = true;

    /// <summary>Skill slugs or names, resolved against every skill this install knows.</summary>
    public List<string> Skills { get; set; } = [];

    public Dictionary<string, string> Environment { get; set; } = [];
    public string? ExtraArguments { get; set; }
    public string Accent { get; set; } = "cyan";
    public bool Enabled { get; set; } = true;
}

/// <summary>A workflow as written in the repository. Steps name their agent.</summary>
public sealed class TeamWorkflowFile
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<TeamWorkflowInputFile> Inputs { get; set; } = [];
    public List<TeamWorkflowStepFile> Steps { get; set; } = [];
    public bool Enabled { get; set; } = true;
}

public sealed class TeamWorkflowInputFile
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Default { get; set; }
    public bool Required { get; set; } = true;
}

public sealed class TeamWorkflowStepFile
{
    public string Name { get; set; } = "Step";
    public int Order { get; set; } = 1;

    /// <summary>Name of the agent that runs this step.</summary>
    public string Agent { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;
    public bool ContinueOnError { get; set; }
    public bool ReuseWorkspace { get; set; } = true;
    public int TimeoutMinutes { get; set; } = 30;
}

/// <summary>A schedule as written in the repository. The target is named, not referenced by id.</summary>
public sealed class TeamScheduleFile
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ScheduleKind Kind { get; set; } = ScheduleKind.Cron;
    public string Cron { get; set; } = "0 * * * *";
    public int IntervalMinutes { get; set; } = 60;
    public string TimeZone { get; set; } = "UTC";
    public ScheduleTarget Target { get; set; } = ScheduleTarget.Agent;

    /// <summary>Name of the agent or workflow this fires.</summary>
    public string TargetName { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;
    public Dictionary<string, string> Inputs { get; set; } = [];

    /// <summary>
    /// Off by default. A schedule arriving from a repository starts something on somebody else's
    /// machine at a time nobody there chose, so it has to be turned on deliberately unless the file
    /// says otherwise.
    /// </summary>
    public bool Enabled { get; set; }

    public bool SkipIfRunning { get; set; } = true;
}
