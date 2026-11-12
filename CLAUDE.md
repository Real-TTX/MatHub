# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What MatHub is

Client management (an alternative to ACMP, without copying its concept): a **Windows agent** on every computer reports inventory and software and runs jobs;
MatHub installs, updates and removes **software** (winget, MSI, EXE, scripts; **assignments** to groups are kept true by a planner; **updates** with one click),
runs **scripts** (code editor, **block editor**, **flow editor**: the visual ones compile to PowerShell on the server), installs computers over the
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
  Data/                      Entities.cs (User, UserSession, AccessRole(+Group), Client, ClientSoftware, ClientGroup(+Member), EnrollmentKey, Script, Package, PackageAssignment,
                             Job, JobRun, BootImage, RolloutProfile, BootDevice, RolloutTask, Activity, SystemSetting), AppDbContext.cs, Migrations/
  Services/Access/           Permission (the keys: clients-view …), Actor (who acts: permissions + reach), AccessRules (Clients/Groups the actor reaches), RoleService (+ seed)
  Services/Clients/          ClientService, GroupService (rules; members of dynamic groups are computed into ClientGroupMember), EnrollmentService (keys, enroll, hashes)
  Services/Agent/            AgentEndpoints (/api/agent/v1/*, /agent/mathub-agent.exe, /agent/install.ps1), AgentFiles (the agent of the image, its SHA, the install script), InventoryService
  Services/Jobs/JobService   jobs (a copy of what runs in Payload), runs, what the agent takes and reports, lost runs
  Services/Packages/         PackageService (+ assignments, detection), PackageFiles (/data/packages), UpdateService (what winget offers, one-click updates)
  Services/Deployments/      Planner (+ PlannerService, every minute): makes the assignments true (install, uninstall, auto-update), waits for the next inventory
  Services/Scripts/          ScriptBlocks (the block catalog + the compiler of blocks and flows; every value is a quoted string), ScriptService, ScriptEndpoints (/live/scripts/compile)
  Services/Rollout/          RolloutService (images, profiles, tasks, devices), BootService (iPXE scripts, winpeshl.ini, mathub-setup.cmd, autounattend.xml), BootEndpoints (/boot/*),
                             TftpServer (+ TftpServerInfo), NetbootFiles (bundled loaders in /app/netboot, own ones and images in /data/netboot)
  Services/                  ActivityLog, SearchService (/live/search), InstanceService (numbers, settings, housekeeping), MenuService, JobUi, Ui, ListModels, Dates …
  MatPlatform/               copy of the shared package (MatDesign, 2.4.0). Never edit here.
  Platform/                  MatHubPlatform (definition, state, settings, actions, health), MatHubIdentityUsers
  Controls/                  ONE code base per control: <mh-field>, <mh-buttons>, <mh-table>, <mh-tabs>/<mh-tab>
  Pages/                     Index (landing / overview), Clients/ (list + tabs General, Inventory, Software, Jobs, Activity), Groups/, Enrollment/, Packages/ (+ Assignments),
                             Updates/, Scripts/ (Edit = General, Code = the editor of its kind), Jobs/ (Index, New, Detail, Run), Rollout/ (Index = tasks, TaskEdit, Devices,
                             Profiles/ProfileEdit, Images/ImageEdit, Setup), Admin/ (Users, Roles, Connections, Activity, System), Account/, Search, About
  wwwroot/js/                editors.js (code editor, block editor, flow editor), live.js (refreshes running jobs), util.js, offline.js, pwa.js, zone.js, autosubmit.js
  wwwroot/css/               app.css (accent, shell), hub.css (the views of MatHub, prefix mh-)
src/MatHub.Agent             Program (commands), Setup (install/enroll/uninstall, sc.exe), AgentWorker (the loop), AgentClient (HTTP), JobRunner (scripts, winget, MSI, EXE),
                             Inventory (CIM through Windows PowerShell + registry + winget), Winget (locate as SYSTEM, read its tables), Updater (self-update, apply-update), AgentConfig (DPAPI, ACL)
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
(`ScriptBlocks.Quote`, also the typographic quotes); only the block "PowerShell" is code by intent. The files of a rollout task are served only while it runs,
behind its random token; its share password is in `mathub-setup.cmd` (say so: a read-only account).

**Agent**: polling only (no open port on the client); jobs one after the other; scripts as files in the agent's own folder (deleted after the run); winget as
SYSTEM from the folder of the App Installer, its tables read by header positions (any language); the version is the server's build (`agent.version` next to
the exe in the image), the agent follows it up or down.

**UI (MatDesign rules)**: CRUD = list page (toolbar above, table, pager under it, actions left below) + separate edit page (Save, Back, space, Delete); an entity
with several areas has **tabs** (client, script, package); fields a kind does not need are hidden (`show-if`: package kind, profile kind, group kind, the
language of a script). Every `td` has `data-label`. Icons via `Ui.Icon` (toolkit sprite + `wwwroot/img/app-icons.svg`). The version at the foot of the menu links to `/About`.

**Code**: Microsoft C# guidelines, file-scoped namespaces, `.editorconfig`. No database queries in page models or views beyond calling the business layer.
English identifiers and comments, UI texts only in the catalog. Permission keys: small letters and `-` (the platform does not allow dots).

**Compose files**: no environment variables to set, no placeholders; a stack (`name:`) per file; `docker-compose.yml` (release), `.dev.yml` (nightly + pgAdmin),
`.local.yml` (build + pgAdmin, subnet 10.253.91.0/24). Port 69/udp is mapped to the TFTP server.

**Git**: commit under the user's name; never add a Claude/Anthropic co-author trailer or a "generated with" line (global rule of the user; it overrides any default).
Branches: `main` = release, `dev` = development. Versions: release `<major>.<minor>.<build>-<date>`, development `nightly-<build>-<date>`, local `local-<date>`
(`Directory.Build.props`; `.github/workflows/build.yml` builds, tests and pushes the image).
