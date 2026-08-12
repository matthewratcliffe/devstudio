# AI Shop Orchestrator

A self-hosted console for running **Claude Code** and **OpenAI Codex** agents side by side. Create
agents, chat with them concurrently, chain them into scheduled workflows, give them repos and
worktrees to work in, and let them talk back to the orchestrator over MCP.

There is **no API integration anywhere**. Every agent drives the real CLI as a child process, so the
only credential involved is the login you complete in the web UI.

## What it does

| Area | What you get |
| --- | --- |
| **Agents** | Any number, each bound to `claude`, `codex` or a CLI you define yourself, with its own system prompt, model, permission mode, skills and MCP servers. |
| **Bring your own CLI** | Describe any installed, already-signed-in CLI on the **CLI providers** page — executable, argument template, output format, sign-in command — and it becomes an agent provider. No code change, no API key. |
| **Concurrent chats** | Sessions run in parallel up to a configurable cap. Each session queues its own turns, so you can keep typing while an agent is working. |
| **Standards** | Global instructions and reference files — setup notes, coding standards — that every agent inherits, staged into each workspace as `./global-files`. |
| **Projects** | Instructions plus uploaded files that reach every agent, session, workflow and schedule in the project. Files are staged into `./project-files` in the workspace. Also where you set the account and the summarisation policy. |
| **Guidance** | Steer an agent that is already working, instead of queueing a turn behind it. Optionally interrupt the turn in flight so the steer lands now. |
| **Multiple accounts** | A personal Claude and a work Claude (and the same for Codex) live side by side as separate credential homes. Pick one per project. |
| **Auto-summarisation** | After N turns a project's sessions roll themselves up and — if you want — restart the CLI conversation from the summary, so long chats stay fast and keep the context that matters. |
| **Workflows** | Ordered steps with a shared run context: every finished step publishes its output as `{{steps.step-name}}`, readable by *all* later steps. Steps sharing an order run at the same time. |
| **Scheduler** | Cron expressions, plain timers, or manual-only saved runs — targeting an agent or a workflow, optionally inside a project folder. |
| **Repositories** | Clone repos from the UI (or pick from `gh repo list`), and cut a fresh git worktree per session so parallel agents never collide. |
| **GitHub and GitLab** | `gh` and `glab` both ship in the image and share the container login, so agents can open pull or merge requests and read issues. A project picks one forge; it can still have several repositories from it. Set `Orchestrator__GitLabHost` (or `GitHubHost`) for a self-managed instance. |
| **Quick chat** | A conversation with no project and no agent — pick a CLI and talk. Read-only, in a scratch directory. |
| **Output files** | Everything an agent writes in its workspace is listed in the chat, with images previewed inline and every file downloadable. |
| **Skills** | Reusable instruction files, written to `.claude/skills/<slug>/SKILL.md` (and mirrored to `AGENTS.orchestrator.md` for Codex) before a session starts. |
| **MCP — both directions** | Register MCP servers and attach them to agents (`.mcp.json` per workspace), **and** the orchestrator exposes its own MCP server at `/mcp` so agents can list sessions, read another agent's transcript, steer a run, leave notes, start sessions and run workflows. HTTP servers can authenticate with an OAuth client-credentials grant, refreshed automatically, or a pasted bearer token. **Test** connects like a CLI would and lists the tools the server actually offers. Servers attach to agents, and a chat — including a quick chat — can carry extra servers of its own, applied from its next turn. The built-in orchestrator entry cannot be deleted and is restored on start if it goes missing. |
| **PWA** | Installable, with a themed offline page. |
| **Persistence** | JSON files on a Docker volume. No database. A volume backup is the whole backup. |

## Running it

```bash
docker compose up -d --build
```

Open <http://localhost:7080>, go to **Logins**, and complete the sign-in for Claude, Codex and
GitHub in the embedded terminal. Each one prints a link and, for Codex and GitHub, a one-time code —
both are pulled out of the scrollback and shown above the terminal with a copy button. Credentials
land in the `ai-shop-home` volume and survive restarts and rebuilds.

Each account picks its own **sign-in method**, because no single flow suits every network:

| Method | What happens | Notes |
| --- | --- | --- |
| **Browser sign-in** | The CLI prints a link and completes the flow through the browser. | Claude asks you to paste a code back. Codex redirects to `http://localhost:1455/auth/callback` — see below, the orchestrator can take that callback for you. |
| **Device code** | A link plus a short code you type into the browser. | Nothing reaches back into the container, so this works from any machine. Available for Codex and GitHub. |
| **Paste a token** | You paste a key or token into a masked field; it goes straight to the CLI's stdin. | Codex (`--with-api-key`) and GitHub (`--with-token`). The CLI stores it in its own credential store — this app never keeps a copy, and the value never appears in the terminal transcript. |
| **Long-lived token** | Claude only: `claude setup-token` mints a durable credential through the browser. | Best for schedules and workflows that run unattended. |
| **Console account** | Claude only: sign in with an Anthropic Console account billed by API usage. | Instead of a Claude subscription. |

### Local development

```bash
dotnet run --project src/AiShop.Ui
```

In `Development` the app stores state under `src/AiShop.Ui/.aishop/` and uses your real home
directory, so it picks up the CLI logins you already have.

## Architecture

Clean architecture, dependencies pointing inwards:

```
src/
  AiShop.Domain          entities only — agents, sessions, projects, workflows, schedules, skills, MCP
  AiShop.Application     abstractions and orchestration — SessionManager, WorkflowEngine, cron parser
  AiShop.Infrastructure  the outside world — CLI adapters, git, gh, JSON stores, pty terminals, scheduler
  AiShop.Ui              Blazor Server console, MCP endpoint, PWA
tests/
  AiShop.Tests           cron, templating, persistence, workflow engine
```

The key seam is `IProviderCli`. `ClaudeCli` runs `claude -p --output-format stream-json` and
`CodexCli` runs `codex exec --json`; both translate CLI output into a common `AgentEvent` stream.
`CustomCli` is the third implementation and it is data-driven: it builds its command from a stored
`CliProvider` definition, so a new tool is a form to fill in rather than code to write.

### Adding a CLI

On **CLI providers**, start from a preset (Copilot, Gemini, Cursor) or fill in:

| Field | Meaning |
| --- | --- |
| Executable | On `PATH` inside the container, or an absolute path. |
| Prompt arguments | e.g. `-p {{prompt}}`. Tokens are substituted *per argument*, so a long prompt stays one argument. An argument whose token resolves to empty is dropped, which is how optional flags work. |
| Resume arguments | e.g. `--resume {{sessionId}}`. Empty means each turn starts fresh. |
| Output format | Plain text, or JSON lines with the text / session-id / error properties named (dotted paths allowed). |
| Sign-in | Login, logout and status commands, plus the text that means signed out. |

Presets are starting points — CLI flags move around, so everything stays editable, and **Test** checks
the executable is actually installed before you wait on a failing session. Custom providers get their
own accounts and per-project account selection, exactly like the built-in two.

Anything the built-ins do that a definition cannot express — Claude's streaming tool events, say — is
why those two remain hand-written.

Sessions are **turn-based rather than a long-lived interactive process**: each turn is a fresh CLI
invocation resumed from the provider's own session id, which is what makes restarts, concurrency and
scheduling reliable.

## Workflow context

Given steps named *Implement*, *Review* and *Fix*, the third step can read both earlier outputs:

```
Original task: {{task}}

Review feedback:
{{steps.review}}

The implementation was:
{{steps.implement}}
```

Available in every step: workflow inputs by name, `{{previous}}`, and `{{steps.<slugified-name>}}`
for any earlier step regardless of distance.

## The orchestrator's MCP server

Attach the built-in `orchestrator` server to an agent and it gains these tools:

`list_agents` · `list_sessions` · `get_session` · `send_message` · `start_session` ·
`send_guidance` · `check_guidance` · `get_notes` · `add_note` · `list_projects` ·
`list_workflows` · `run_workflow`

That is how a manager agent supervises worker agents — it can read what they produced, steer them
mid-run, and respond.

## Guidance

A turn is one CLI invocation and its prompt cannot be rewritten once the process has it, so a steer
reaches the agent by three routes at once:

1. **On disk** — written to `GUIDANCE.md` in the workspace. Every agent is told to re-read it during
   long tasks.
2. **Over MCP** — a running agent calls `check_guidance` with its own session id and gets whatever is
   waiting. This is the only route that lands *inside* a turn already in progress.
3. **Next turn** — outstanding guidance is folded into the top of the next prompt regardless, so it
   can never be missed.

Ticking **interrupt** stops the turn in flight (just that turn — the session and its queue survive)
and immediately starts a new one carrying the steer.

## One provider per project

A project pins **one AI provider** and **one forge**. Every session inside it runs on that provider
whatever the individual agent is configured with, so a project can never end up half on Claude and
half on Codex; the stored agent is left untouched, the override lives only for the session. The
repository picker narrows to the project's forge, and a project can have as many repositories from it
as you like.

Leave the AI provider unset and each agent keeps its own choice.

## Files an agent produces

The chat side panel lists the session's working directory, newest first, skipping noise like `.git`
and `node_modules`. Images are previewed inline; every file has a download link. The list refreshes
itself when a turn finishes.

Downloads go through `/workspace/{sessionId}/{path}`, which resolves the path and refuses anything
that lands outside that session's own directory — the session id and path both arrive from the
browser, so neither is trusted.

### MCP tools and permission modes

Two things bite when attaching an MCP server to an agent, and the app now handles both:

- **Tools must be allow-listed.** Running headless there is nobody to answer a permission prompt, so
  every attached server's tools are passed as `--allowedTools mcp__<server>`. Without this the CLI
  reports *"Claude requested permissions… but you haven't granted it yet"* and the model concludes the
  service simply is not available.
- **Plan mode refuses MCP tool calls outright**, whatever the tool does. An agent set to Plan sees the
  tools but cannot call them. The agent editor and the chat panel both warn when that combination is
  set, and a quick chat with servers attached automatically runs in Accept edits instead.

Connection status is surfaced too: the transcript shows `mcp:<server> connected`, or an error naming
the server if it failed to start, rather than the tools silently going missing.

### Images

Neither `claude` nor `codex` generates images, so nothing here can conjure one on its own. What works
today:

- **An image-generating MCP server.** Register it on the MCP page (with OAuth or a token if it needs
  one), attach it to an agent or a single chat, and the agent can call it. Whatever it writes into the
  workspace shows up in the files panel.
- **A custom CLI provider.** If you have an image tool on the command line, describe it on the CLI
  providers page and drive it as an agent.
- **Code the model can write.** SVG, and diagrams via mermaid, are text — agents produce these
  directly and they preview inline as images.

## Picking rather than typing

Model, thinking level and base branch are all dropdowns rather than free text:

- **Model** is offered per provider from `Orchestrator__ClaudeModels` / `CodexModels`, or the model
  list on a custom CLI definition. Names move faster than this app, so the list is configuration and
  there is always a *Something else…* option that reveals a text box.
- **Thinking level** maps to what each CLI actually accepts — `--effort` for Claude
  (low → max), `model_reasoning_effort` for Codex (minimal → high), and whatever a custom CLI
  declares.
- **Base branch** is read live from the selected repository with `git for-each-ref`, and falls back
  to a text box if the repository cannot be listed.

## Instruction layering

Three layers reach an agent's system prompt, least specific first:

    Standards (global)  →  Project instructions  →  Agent instructions

**Standards** is the page for house rules that never change per project: how code should be written,
how to commit, what to avoid. Files uploaded there are staged into `./global-files` in every
workspace and listed in the prompt so the agent knows to read them; project files land in
`./project-files` the same way. A project that plays by different rules can untick **apply the global
standards and files here** and skip the layer entirely.

### The Codex callback

Codex hard-codes `http://localhost:1455/auth/callback` into its authorise request, which only
resolves on the machine running the container. Two ways round it, both built in:

- **Change the port.** When the browser lands on the dead `localhost:1455` URL, edit the port in the
  address bar to `7080`. This app serves the same `/auth/callback` path and hands it to the CLI.
- **Paste the URL.** The sign-in terminal shows a field for the failed callback URL — paste the whole
  thing and it is delivered for you.

Only the path and query of what you paste are used; the request always goes to loopback on the
configured port, so it cannot be pointed anywhere else. Set `Orchestrator__CliCallbackPort` if you
ever need a different port.

Device code sign-in avoids all of this, which is why it is the default for Codex.

## Accounts

Each account is a separate home directory holding its own `.claude` / `.codex` credentials, and the
CLI process runs with `HOME` pointed at it. Add accounts on the **Logins** page, log into each one,
then choose per project which Claude and which Codex account its work runs under. Resolution order:

    project's choice for that provider → agent's pinned account → provider default → container home

The seeded `Default` account for each provider points at the container home, so an existing login
keeps working untouched.

## Summarisation

Set **summarise after N turns** on a project. When a session hits the threshold the same agent is
asked to summarise the work, the summary is stored on the session and shown in the transcript, and —
with **restart the conversation from the summary** on — the provider's conversation id is dropped so
the next turn opens a fresh CLI conversation with the summary as its context. Leave it off to keep
the full history and just record the summary.

## Configuration

Everything is under the `Orchestrator` configuration section, overridable with
`Orchestrator__<Key>` environment variables:

| Key | Default | Purpose |
| --- | --- | --- |
| `DataPath` | `/data` | Root of the state volume. |
| `RepositoriesPath` | `/data/repos` | Clones. |
| `WorktreesPath` | `/data/worktrees` | Per-session worktrees. |
| `ScratchPath` | `/data/scratch` | Workspace for agents with no repo or project. |
| `HomePath` | `/home/orchestrator` | The default account's home: `~/.claude`, `~/.codex` and the `gh` login. Extra accounts live under `<DataPath>/accounts/`. Empty means the real user home. |
| `MaxConcurrentSessions` | `6` | Cap on agent turns running at once. |
| `TurnTimeoutMinutes` | `60` | A turn is abandoned after this long. |
| `SchedulerTickSeconds` | `20` | How often due schedules are checked. |
| `PruneEphemeralWorktrees` | `false` | Delete a session's worktree when it finishes. Off, because uncommitted work would go with it. |

## Permission modes

| Mode | Claude | Codex |
| --- | --- | --- |
| Default | `--permission-mode default` | `--sandbox read-only` |
| Plan | `--permission-mode plan` | `--sandbox read-only` |
| Accept edits | `--permission-mode acceptEdits` | `--sandbox workspace-write` |
| No restrictions | `--dangerously-skip-permissions` | `--dangerously-bypass-approvals-and-sandbox` |

Unattended work (schedules, workflows) needs **Accept edits** or higher — in Default mode the CLI
blocks waiting for an approval nobody is there to give. Use it with a worktree.

## Tests

```bash
dotnet test
```
