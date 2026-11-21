# COMS-MVC

ASP.NET Core MVC app: EF Core on PostgreSQL (Npgsql), Identity, SignalR hubs, Razor views, xUnit tests in `COMS.Tests` (SQLite).

- Build: `dotnet build "COMS MVC.csproj"`
- Test: `dotnet test COMS.Tests`

## Multi-agent workflow

Project subagents live in `.claude/agents/`:

| Agent | Role | Edits? |
|---|---|---|
| `frontend-developer` | Razor views, HTML/CSS/JS, responsive UI/UX | yes |
| `backend-developer` | controllers, services, APIs, hubs, business logic | yes |
| `database-specialist` | DbContext, migrations, SQL, query optimization | yes (never data) |
| `security-reviewer` | auth, validation, vulnerabilities | read-only |
| `code-reviewer` | bugs, regressions, duplication, architecture | read-only |
| `performance-optimizer` | slow queries, wasted work, memory, frontend perf | read-only |

Rules for the main agent:
- The main agent owns architecture, coordination and final integration.
- Delegate independent tasks to separate subagents when practical; run independent research/review in parallel.
- Never assign two agents to edit the same file. Give each agent an explicit file list and one responsibility.
- Use the read-only agents for research and review.
- Do not spawn agents for simple tasks; do them directly.
- Ask subagents for concise findings and actionable fixes, not long reports.

## Implementation rules
- Inspect the existing architecture before changing it; preserve existing functionality.
- Do not rewrite unrelated files.
- Do not change database schemas without a clear requirement.
- Never modify or delete existing data without explicit authorization.
- Do not install unnecessary packages. Do not create backups unless asked.
- Never claim something works without verifying it; run build/tests when authorized and report the real result.
