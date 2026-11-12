# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What MatHub is

Client management (an alternative to ACMP, without copying its concept): a **Windows agent** on every computer reports inventory and software and runs jobs;
MatHub installs, updates and removes **software** (winget, MSI, EXE, scripts; **assignments** to groups are kept true by a planner; **updates** with one click),
runs **scripts** (code editor, **block editor**, **flow editor**: the visual ones share one catalog of actions and compile to PowerShell on the server; dialogs
and wizards for the person at the computer, **secrets** for passwords), chains them in **sequences** (one job, step by step, across restarts), offers them in the
**kiosk** of the agent (people start them themselves), installs computers over the
**network** (TFTP + iPXE built in, Windows PE through wimboot with an answer file MatHub writes, Linux, own iPXE scripts) and has **roles** with permissions for
all clients or for groups. Part of the Mat family on **MatPlatform 2.4** (MatDesign `docs/PLATFORM.md`): consumes `identity.oidc` (MatIdentity) and `mail.smtp`
(MatSMTP), both optional; contributes the launcher MatHub and two admin widgets; declares the roles `admin`, `technician`, `operator`, `viewer` and its
permissions, so MatIdentity can give them.

Stack: ASP.NET Core on **.NET 10**, C#, **Razor Pages**, EF Core + **PostgreSQL**, the **MatUi** toolkit of MatDesign (`wwwroot/matui`, release 0.9.3, copied:
never edit by hand), plain JavaScript without a build step. The agent: .NET 10 Worker Service for Windows, one self-contained file. Docker-first. Web port
**4910** (pgAdmin 4911, local Postgres 4912), TFTP 4969/udp in the container (69 outside). UI languages: English and German (`Resources/Texts/*.json`).
Accent: amber `#b45309` (white text 5.0:1) – not yet in MatDesign's registry (`AppRegistry.cs`, `matui.apps.css`): add it there.

## Build / run / test

- **Build:** `dotnet build MatHub.slnx` (SDK 10, `global.json`). Expected: 0 warnings, 0 errors. The agent (`net10.0-windows`) builds on Linux too (`EnableWindowsTargeting`).
- **Tests:** `dotnet test`. Database tests run when `MATHUB_TEST_DB` is set (an administrator connection string, e.g.
  `Host=localhost;Port=4912;Username=mathub;Password=mathub;Database=postgres` while the local stack runs); each test makes and drops a database of its own.
  `TextCatalogTests` checks every key of the code and the scripts in both languages; `ScriptTests` the compiler; `BootTests` the answer file and the iPXE scripts;
  `AgentTests` the winget tables (the agent's `Winget.cs` and `ProcessRunner.cs` are compiled into the tests); `HubTests` enrollment, jobs, roles, the planner.
- **Run / test the app: always rebuild the container** (`./scripts/redeploy.ps1`, `-Clean` for empty volumes). CSS, JS and cshtml are baked into the image.
- **Test account (always):** `admin@example.com` / `Start12345` – the first account of a fresh stack (it becomes the administrator).
- **The agent's windows** can be tested as a normal user too: `mathub-agent run` and `mathub-agent tray --auto --snapshot <folder>` with the same
  `MATHUB_AGENT_DATA` (it names the pipe), `kiosk --snapshot file.png`; `MATHUB_AGENT_LANGUAGE=de|en`, `MATHUB_AGENT_COMPUTER` (the name in the pictures).
  Never run a script with a restart, a rename or a domain join on the developer's computer.
- **Real tests are end to end** in a browser (Playwright with the installed Edge, `channel: 'msedge'`) – and with the real agent: `MATHUB_AGENT_DATA=<folder>`
  lets a normal user run `mathub-agent enroll --server http://localhost:4910 --key …` and `mathub-agent run` without installing the service. Never put the
  inventory of a real computer into the docs: the screenshots use generic clients sent through the agent API.
- **Migrations:** `dotnet ef migrations add <Name> -p src/MatHub -o Data/Migrations` (local tool in `dotnet-tools.json`). Applied at start-up.
- **MatPlatform:** `node ../MatDesign/tools/matplatform-copy.js src/MatHub` (never edit `MatPlatform/` here).
- **Logo:** `node tools/logo.js` writes every SVG level (mark, word marks, favicon, maskable, mono, the sprite glyph); `tools/logo-png.js` renders the PNGs (Edge).
- Write files with the editor tools, not with shell heredocs (apostrophes and backslashes get mangled).

## Map

```
src/MatHub.Shared/AgentProtocol.cs   THE protocol of server and agent (DTOs, job kinds, headers), compiled into both projects
src/MatHub
  Program.cs                 composition root: services, cookie auth (+ MatIdentity), rate limits (signin 10/min, enroll 60/min), static files, endpoints
  Infrastructure.cs          HealthCheck (--healthcheck), Database (migrate with retry, seed the built-in roles), SecurityHeaders (CSP), MaintenanceService (hourly), EmptyStringBinderProvider
  Data/                      Entities.cs (User, UserSession, AccessRole(+Group), Client, ClientSoftware, ClientGroup(+Member), EnrollmentKey, Script, Package, PackageVersion, PackageInstaller, PackageAssignment, CatalogPackage,
                             Job, JobRun, Secret, Sequence, KioskItem, BootImage, RolloutProfile, BootDevice, RolloutTask, Activity, SystemSetting), AppDbContext.cs, Migrations/
  Services/Access/           Permission (the keys: clients-view …), Actor (who acts: permissions + reach), AccessRules (Clients/Groups the actor reaches), RoleService (+ seed)
  Services/Clients/          ClientService, GroupService (rules: one or more conditions, all or any; members of dynamic groups are computed into ClientGroupMember), EnrollmentService (keys, enroll, hashes)
  Services/Agent/            AgentEndpoints (/api/agent/v1/*, /agent/mathub-agent.exe, /agent/install.ps1), AgentFiles (the agent of the image, its SHA, the install script), InventoryService
  Services/Jobs/             JobService (jobs: a copy of what runs in Payload, of a sequence in Steps; runs, what the agent takes – with the secrets its script names –
                             and reports, lost runs, the state for the kiosk), SequenceService (sequences: steps, the copy a job gets)
  Services/Kiosk/            KioskService (offers for all clients or groups; the live offer for an agent; a start = a job for that client, origin "kiosk")
  Services/Packages/         PackageService (what a package is, assignments, detection), PackageVersions (versions New/Testing/Approved/Retired, installers per platform,
                             the AgentJob of a version with its Alternatives per platform), PackageFiles (/data/packages/{package}/{installer}/, staging of the assistant),
                             InstallerAnalysis (MSI: Property table via OpenMcdf; EXE: setup marks, PE machine), WingetCatalog (the store: index of winget → CatalogPackage,
                             manifests, icons, add, prepare new versions), UpdateService (what waits, what is newer, who is behind; unmanaged updates only shown)
  Services/Deployments/      Planner (+ PlannerService, every minute): makes the assignments true (install, uninstall, auto-update), waits for the next inventory
  Services/Scripts/          ScriptBlocks (THE catalog of actions + the compilers of blocks and flows, the prelude: $MatHubVars, Invoke-MatHubDialog, Get-MatHubSecret;
                             BlocksToFlow), ScriptSteps (the PowerShell of each action and check; dialogs as UiDialog JSON), ScriptTemplates, ScriptService,
                             ScriptFiles (ScriptFileStore /data/scripts/{id}/, ScriptFileService: uploads, the files of a job, AllowedAsync), SecretService (DataProtection; ResolveAsync for a run), ScriptTester (a test from the editor on the person's computer, User.TestClientId;
                             trial runs: CompileBlocks/CompileFlow(dryRun) say what an action would do – DryRunSafe runs as it is – code is only parsed),
                             ScriptEndpoints (/live/scripts/compile, /live/scripts/{id}/test, /live/runs/{id})
  Services/Rollout/          RolloutService (images, profiles, tasks, devices), BootService (iPXE scripts, winpeshl.ini, mathub-setup.cmd, autounattend.xml), BootEndpoints (/boot/*),
                             TftpServer (+ TftpServerInfo), NetbootFiles (bundled loaders in /app/netboot, own ones and images in /data/netboot)
  Services/                  ActivityLog, SearchService (/live/search), InstanceService (numbers, settings, housekeeping), MenuService, JobUi, Ui, ListModels, Dates …
  MatPlatform/               copy of the shared package (MatDesign, 2.4.0). Never edit here.
  Platform/                  MatHubPlatform (definition, state, settings, actions, health), MatHubIdentityUsers
  Controls/                  ONE code base per control: <mh-field>, <mh-buttons>, <mh-table>, <mh-tabs>/<mh-tab>, <mh-stats>/<mh-stat> (a row of numbers)
  Pages/                     Index (landing; _Dashboard = the overview), Clients/ (list + tabs General, Inventory, Software, Jobs, Activity), Groups/, Enrollment/, Packages/ (Index, New = the assistant, Edit = General, Versions, Assignments), Store/ (Index, Item),
                             Updates/, Scripts/ (Edit = General + templates, Code = the editor of its kind, Files), Secrets/, Sequences/ (+ _Step), Kiosk/, Jobs/ (Index, New = now or later, Detail, Run), Groups/_Condition (a row of a rule), Rollout/ (Index = tasks, TaskEdit, Devices,
                             Profiles/ProfileEdit, Images/ImageEdit, Setup), Admin/ (Users, Roles, Connections, Activity, System), Account/, Search, About
  wwwroot/js/                editors.js (code editor with toolbar/search/snippets, block editor, flow editor with zoom; the searchable palette, the field editor of dialogs),
                             scripttest.js (the test card of the editor), sequence.js (the steps of a sequence), kiosk.js (the preview of an offer), conditions.js (the rows of a group rule), live.js (refreshes running jobs), util.js, offline.js, pwa.js, zone.js, autosubmit.js
  wwwroot/css/               app.css (accent, shell), hub.css (the views of MatHub, prefix mh-)
src/MatHub.Agent             Program (commands), Setup (install/enroll/uninstall, sc.exe), AgentWorker (the loop), AgentClient (HTTP), JobRunner (scripts, winget, MSI, EXE),
                             Inventory (CIM through Windows PowerShell + registry + winget), Winget (locate as SYSTEM, read its tables), Updater (self-update, apply-update), AgentConfig (DPAPI, ACL),
                             UiBroker (the named pipe of the service: kiosk, dialogs; AgentSignal wakes the loop; UiPipe = the client, `ask`; PipeTrust checks the server process)
  src/MatHub.Agent/Ui        WinForms in the session of the person: TrayApp (`tray`: symbol, dialog loop), KioskWindow (`kiosk`), DialogWindow (message, question, form/wizard),
                             ProgressWindow, Theme (colours, Segoe UI Variable / Fluent Icons), UiText (de/en). `--snapshot <png|folder>` + `--auto` draw them without showing (tests, docs)
netboot/mathub-embed.ipxe    the script built into MatHub's iPXE (Dockerfile stage netboot)
tests/MatHub.Tests           unit + database tests
tools/                       logo.js, logo-png.js
```

## Conventions

**Database (PostgreSQL)**: tables PascalCase and singular, key always `Id` BIGINT, every record has `CreateDate/CreateUserId/UpdateDate/UpdateUserId` (filled by
`AppDbContext.SaveChanges`), `UpdateState` (0 deleted, 1 created, 2 updated) for soft delete. A `Token` (uuid) where a key is a secret or an address
(`UserSession.Token`, `Client.Token`, `RolloutTask.Token`); secrets only as hashes (`Client.SecretHash`, `EnrollmentKey.KeyHash`) or encrypted with DataProtection
(the passwords of rollout profiles).

**Security / permissions**: the business layer is static classes that take an `Actor` and check it in every function (permission + reach); a page only hides
what the actor may not do. Something an actor does not reach answers 404 (`Outcome.Denied`). Administrators may do everything; users have one `AccessRole`
(permissions, all clients or groups) plus what MatIdentity adds (`User.IdentityPermissions`). The agent API has no cookie: `X-MatHub-Client` + `X-MatHub-Key`,
compared in constant time; a removed client gets 401, a switched-off one 403. Script values of the visual editors become single-quoted PowerShell strings
(`ScriptBlocks.Quote`, also the typographic quotes); `{{name}}` in a value becomes `[string]$MatHubVars['name']` (names are letters, digits, _), `./name` a file of the script (`Join-Path $MatHubFiles`, the folder the agent runs the script in: `MATHUB_FILES`), `%VAR%` is expanded on the computer – every expression is in parentheses (an argument of a command); only the
block "PowerShell" and the check "PowerShell is true" are code by intent. A new action: an entry in `ScriptBlocks.Types`, its PowerShell in `ScriptSteps`, its
texts (`Blocks.Type/Hint/Field/Option.*`); `ScriptTests` compiles every action. Secrets reach only the run whose script names them (`SecretsIn`), as
environment variables; the agent masks their values. The kiosk pipe: every signed-in user may use it (it offers what the kiosk shows anyway); the service
creates it first, clients check the server is the installed agent. A culture set inside an async method is gone when it returns: return it and set it in the caller. The files of a rollout task are served only while it runs,
behind its random token; its share password is in `mathub-setup.cmd` (say so: a read-only account).

**Software**: a package is the software, its versions carry installers per platform; only an approved version is distributed (a version in test to the
pilot group of the package); new versions of the catalog are prepared, never approved by MatHub itself (the user's decision); no path may install an unapproved
version (no winget upgrade as a job or a step any more – the enum stays for old jobs). Versions are compared like winget (`WingetCatalog.CompareVersions`);
"latest" (packages from before the versions) is no version: installed is enough (`PackageVersions.Comparable`).

**Agent**: polling only (no open port on the client); jobs one after the other; scripts as files in the agent's own folder (deleted after the run); winget as
SYSTEM from the folder of the App Installer, its tables read by header positions (any language); the version is the server's build (`agent.version` next to
the exe in the image), the agent follows it up or down.

**UI (MatDesign rules)**: CRUD = list page (toolbar above, table, pager under it, actions left below) + separate edit page (Save, Back, space, Delete); an entity
with several areas has **tabs** (client, script, package); fields a kind does not need are hidden (`show-if`: package kind, profile kind, group kind, the
language of a script). Every `td` has `data-label`. Moments in lists are `Ui.When` ("5 min ago", the exact time as its title); operating systems `Ui.Os` (without "Microsoft"); a row of numbers is `<mh-stats>`; actions of a record are a `mat-itemlist` of rows with a hint (client, script). Cards one under the other in a column keep their distance (hub.css). Icons via `Ui.Icon` (toolkit sprite + `wwwroot/img/app-icons.svg`). The version at the foot of the menu links to `/About`.

**Code**: Microsoft C# guidelines, file-scoped namespaces, `.editorconfig`. No database queries in page models or views beyond calling the business layer.
English identifiers and comments, UI texts only in the catalog. Permission keys: small letters and `-` (the platform does not allow dots).

**Compose files**: no environment variables to set, no placeholders; a stack (`name:`) per file; `docker-compose.yml` (release), `.dev.yml` (nightly + pgAdmin),
`.local.yml` (build + pgAdmin, subnet 10.253.91.0/24). Port 69/udp is mapped to the TFTP server.

**Git**: commit under the user's name; never add a Claude/Anthropic co-author trailer or a "generated with" line (global rule of the user; it overrides any default).
Branches: `main` = release, `dev` = development. Versions: release `<major>.<minor>.<build>-<date>`, development `nightly-<build>-<date>`, local `local-<date>`
(`Directory.Build.props`; `.github/workflows/build.yml` builds, tests and pushes the image).
