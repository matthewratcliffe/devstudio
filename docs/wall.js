// The hero is a wall of concurrent sessions, because that is what the thing does: several agents
// working at once, each in its own checkout, streaming as they go. The panes replay a short scripted
// run rather than pretending to be live — the timings and the shapes are the ones the app produces.

(function () {
    const sessions = [
        {
            agent: 'reviewer',
            provider: 'claude',
            state: 'running',
            script: [
                { tool: 'Bash', arg: 'glab mr diff 412', took: '1.4s' },
                { say: 'Two things in the retry loop: the delay is not' },
                { say: 'jittered, and a 429 is retried forever.' },
                { tool: 'Read', arg: 'src/Http/RetryPolicy.cs', took: '38ms' },
                { say: 'Suggesting a cap and full jitter.', caret: true }
            ]
        },
        {
            agent: 'nightly-triage',
            provider: 'codex',
            state: 'done',
            script: [
                { tool: 'shell', arg: 'glab issue list --label bug', took: '900ms' },
                { say: '9 open, 3 opened since yesterday.' },
                { tool: 'shell', arg: 'dotnet test', took: '2m 14s' },
                { say: 'One failure, and it is the flaky clock test' },
                { say: 'again. Filed #218 with the seed.' }
            ]
        },
        {
            agent: 'docs',
            provider: 'llama.cpp',
            state: 'waiting',
            script: [
                { tool: 'list_files', arg: 'docs/', took: '4ms' },
                { tool: 'read_file', arg: 'docs/upgrading.md', took: '11ms' },
                { say: 'The 2.x steps still reference the old flag.' },
                { say: 'Rewrite that section, or leave a note?' }
            ]
        },
        {
            agent: 'migrator',
            provider: 'opencoder',
            state: 'running',
            script: [
                { tool: 'Bash', arg: 'git worktree list', took: '22ms' },
                { say: 'Working in a fresh worktree off main.' },
                { tool: 'Edit', arg: 'src/Domain/Order.cs', took: '61ms' },
                { tool: 'Edit', arg: 'src/Domain/Invoice.cs', took: '44ms' },
                { say: '11 files to go.', caret: true }
            ]
        }
    ];

    const wall = document.querySelector('.wall');
    if (!wall) return;

    const still = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    function lineFor(step) {
        const line = document.createElement('div');
        line.className = step.tool ? 'line tool' : 'line';

        if (step.tool) {
            line.innerHTML =
                '<span class="name"></span><span class="arg"></span><span class="took"></span>';
            line.querySelector('.name').textContent = step.tool;
            line.querySelector('.arg').textContent = step.arg;
            line.querySelector('.took').textContent = step.took;
        } else {
            line.textContent = step.say;
            if (step.caret) line.insertAdjacentHTML('beforeend', '<span class="caret"></span>');
        }

        return line;
    }

    sessions.forEach(function (session, index) {
        const pane = document.createElement('div');
        pane.className = 'pane';
        pane.dataset.state = session.state;
        pane.innerHTML =
            '<div class="pane-head">' +
            '<span class="pane-agent"></span>' +
            '<span class="pane-provider"></span>' +
            '<span class="dot" aria-hidden="true"></span>' +
            '</div><div class="pane-body"></div>';

        pane.querySelector('.pane-agent').textContent = session.agent;
        pane.querySelector('.pane-provider').textContent = session.provider;

        // Screen readers get the state as words; sighted readers get the dot.
        pane.setAttribute('aria-label', session.agent + ' session, ' + session.state);

        const body = pane.querySelector('.pane-body');
        wall.appendChild(pane);

        if (still) {
            session.script.forEach(function (step) { body.appendChild(lineFor(step)); });
            return;
        }

        // Staggered per pane, so the wall fills the way four sessions actually would.
        session.script.forEach(function (step, position) {
            const line = lineFor(step);
            line.style.animationDelay = (index * 220 + position * 480) + 'ms';
            body.appendChild(line);
        });
    });

    const copy = document.querySelector('.command button');
    if (copy) {
        copy.addEventListener('click', async function () {
            try {
                await navigator.clipboard.writeText(copy.dataset.copy);
                const was = copy.textContent;
                copy.textContent = 'copied';
                setTimeout(function () { copy.textContent = was; }, 1400);
            } catch {
                copy.textContent = 'select it';
            }
        });
    }
})();
