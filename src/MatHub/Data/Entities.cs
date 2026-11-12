namespace MatHub.Data;

/// <summary>What every record has: an Id (BIGINT) and who created and changed it, and when (the columns are filled by <see cref="AppDbContext"/>).</summary>
public abstract class Entity
{
    public long Id { get; set; }

    public DateTime CreateDate { get; set; }

    public long? CreateUserId { get; set; }

    public DateTime UpdateDate { get; set; }

    public long? UpdateUserId { get; set; }
}

/// <summary>A record that supports soft delete: 0 = deleted, 1 = created, 2 = updated. A deleted record is hidden by a query filter.</summary>
public abstract class SoftEntity : Entity
{
    public const short Deleted = 0;
    public const short Created = 1;
    public const short Updated = 2;

    public short UpdateState { get; set; } = Created;
}

/// <summary>The account type: an administrator may do everything (people, roles, settings); a user may do what the <see cref="Role"/> of the account allows.</summary>
public enum UserRole
{
    Admin = 1,
    User = 2,
}

// ==== People ==========================================================================================================================

public sealed class User : SoftEntity
{
    /// <summary>The address to sign in with; unique, stored in lower case.</summary>
    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>The permission set of a user (not of an administrator); null = no permission at all (the person sees only the start page).</summary>
    public long? AccessRoleId { get; set; }

    /// <summary>The client the person tests scripts on ("my computer"), chosen in the script editor.</summary>
    public long? TestClientId { get; set; }

    public AccessRole? AccessRole { get; set; }

    /// <summary>
    /// The permissions MatIdentity added (claim <c>entitlements</c>, at the last sign-in), comma separated; they come on top of those of <see cref="AccessRole"/>.
    /// Empty while MatIdentity does not decide the roles of MatHub.
    /// </summary>
    public string IdentityPermissions { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>The IANA time zone dates are shown in.</summary>
    public string TimeZone { get; set; } = "Europe/Berlin";

    /// <summary>The language of the interface: en or de; empty = the language of the browser.</summary>
    public string Language { get; set; } = string.Empty;

    public int FailedLogins { get; set; }

    public DateTime? LockoutUntil { get; set; }

    public DateTime? LastLoginDate { get; set; }

    /// <summary>system, light or dark.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>standard, paper, contrast or oled.</summary>
    public string Scheme { get; set; } = "standard";

    /// <summary>The person of MatIdentity (the claim <c>sub</c>) this account is linked with; null = a local account only.</summary>
    public string? ExternalSubject { get; set; }
}

/// <summary>A signed-in browser. The cookie carries the <see cref="Token"/>; the session lives in the database.</summary>
public sealed class UserSession : Entity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long UserId { get; set; }

    public User? User { get; set; }

    public DateTime LastSeenDate { get; set; }

    public DateTime ExpiresDate { get; set; }

    public bool IsRevoked { get; set; }

    public string? UserAgent { get; set; }

    public string? IpAddress { get; set; }

    /// <summary>The ID token of a sign-in through MatIdentity (for the sign-out there); null for a local sign-in. A secret: never exported.</summary>
    public string? IdentityToken { get; set; }

    /// <summary>The session of MatIdentity (the claim <c>sid</c>) this session started with: the back-channel logout ends it by this.</summary>
    public string? IdentitySessionId { get; set; }
}

/// <summary>
/// A set of permissions (<see cref="Services.Permission"/>) and the clients they apply to: all clients, or the members of some groups. The built-in sets have a
/// <see cref="Key"/> (the role names of the platform definition: <c>technician</c>, <c>operator</c>, <c>viewer</c>) so MatIdentity can name them.
/// </summary>
public sealed class AccessRole : SoftEntity
{
    /// <summary>The key MatIdentity uses in the claim <c>role</c>; null for a set made by an administrator.</summary>
    public string? Key { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The permissions, comma separated (<c>clients-view,scripts-run</c>).</summary>
    public string Permissions { get; set; } = string.Empty;

    /// <summary>True: every client; false: only the members of <see cref="Groups"/>.</summary>
    public bool AllClients { get; set; } = true;

    public ICollection<AccessRoleGroup> Groups { get; set; } = [];
}

/// <summary>A group whose clients an <see cref="AccessRole"/> may reach (when it is not for all clients).</summary>
public sealed class AccessRoleGroup : Entity
{
    public long AccessRoleId { get; set; }

    public AccessRole? AccessRole { get; set; }

    public long ClientGroupId { get; set; }

    public ClientGroup? ClientGroup { get; set; }
}

// ==== Clients =========================================================================================================================

public enum ClientStatus
{
    /// <summary>Enrolled with a key that needs an approval: the agent asks, but receives no work.</summary>
    Pending = 1,

    Active = 2,

    /// <summary>Switched off by an administrator: the agent is refused.</summary>
    Disabled = 3,
}

/// <summary>A managed computer with the MatHub agent. The agent signs its calls with <see cref="Token"/> and a secret of which only the hash is kept.</summary>
public sealed class Client : SoftEntity
{
    /// <summary>The public identity of the agent (header <c>X-MatHub-Client</c>).</summary>
    public Guid Token { get; set; } = Guid.NewGuid();

    /// <summary>SHA-256 (hex) of the secret of the agent (header <c>X-MatHub-Key</c>).</summary>
    public string SecretHash { get; set; } = string.Empty;

    public ClientStatus Status { get; set; } = ClientStatus.Active;

    /// <summary>The computer name the agent reports.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Free tags, comma separated.</summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>A hash of the MachineGuid of Windows: recognises a reinstall of the agent.</summary>
    public string? MachineId { get; set; }

    public string? Domain { get; set; }

    public string? OperatingSystem { get; set; }

    public string? OsVersion { get; set; }

    public string? OsBuild { get; set; }

    public string? Architecture { get; set; }

    public string? Manufacturer { get; set; }

    public string? Model { get; set; }

    public string? SerialNumber { get; set; }

    public string? Processor { get; set; }

    public long? MemoryBytes { get; set; }

    public long? DiskTotalBytes { get; set; }

    public long? DiskFreeBytes { get; set; }

    /// <summary>The IPv4/IPv6 addresses, comma separated.</summary>
    public string? IpAddresses { get; set; }

    /// <summary>The MAC addresses (lower case, colons), comma separated.</summary>
    public string? MacAddresses { get; set; }

    public string? LastUser { get; set; }

    public DateTime? BootDate { get; set; }

    public string? AgentVersion { get; set; }

    public DateTime? LastSeenDate { get; set; }

    public DateTime? LastInventoryDate { get; set; }

    /// <summary>Set to ask the agent for an inventory with its next call.</summary>
    public bool InventoryRequested { get; set; }

    /// <summary>Updates winget offers for the client (counted at the last inventory).</summary>
    public int UpdatesAvailable { get; set; }

    /// <summary>The whole inventory of the last report (disks, network adapters …) as JSON.</summary>
    public string? Inventory { get; set; }

    public long? EnrollmentKeyId { get; set; }
}

/// <summary>A program installed on a client (from the registry, and from winget with an available update).</summary>
public sealed class ClientSoftware : Entity
{
    public long ClientId { get; set; }

    public Client? Client { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Version { get; set; }

    public string? Publisher { get; set; }

    /// <summary>The id of the package at winget, when winget knows it.</summary>
    public string? WingetId { get; set; }

    /// <summary>The newer version winget offers; null = up to date (or unknown).</summary>
    public string? AvailableVersion { get; set; }
}

public enum GroupKind
{
    /// <summary>Clients are added by hand.</summary>
    Static = 1,

    /// <summary>Clients belong by a rule over their inventory; membership is computed.</summary>
    Dynamic = 2,
}

public enum GroupRuleField
{
    Name = 1,
    OperatingSystem = 2,
    Domain = 3,
    Manufacturer = 4,
    Model = 5,
    IpAddress = 6,
    Tag = 7,
}

public enum GroupRuleOperator
{
    Contains = 1,
    Equals = 2,
    StartsWith = 3,
    EndsWith = 4,
}

/// <summary>A group of clients: a department, a location, the laptops. Groups are the targets of deployments and the scope of permissions.</summary>
public sealed class ClientGroup : SoftEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>A colour chosen by the user (#rrggbb).</summary>
    public string? Colour { get; set; }

    public GroupKind Kind { get; set; } = GroupKind.Static;

    public GroupRuleField RuleField { get; set; } = GroupRuleField.Name;

    public GroupRuleOperator RuleOperator { get; set; } = GroupRuleOperator.Contains;

    public string? RuleValue { get; set; }

    /// <summary>The conditions after the first one (JSON: field, operator, value); the first one is RuleField, RuleOperator, RuleValue.</summary>
    public string? MoreRules { get; set; }

    /// <summary>A client is a member when one condition matches (false: when all of them match).</summary>
    public bool MatchAny { get; set; }

    public ICollection<ClientGroupMember> Members { get; set; } = [];
}

/// <summary>A client in a group: added by hand (static group) or computed from the rule (dynamic group).</summary>
public sealed class ClientGroupMember : Entity
{
    public long ClientGroupId { get; set; }

    public ClientGroup? ClientGroup { get; set; }

    public long ClientId { get; set; }

    public Client? Client { get; set; }
}

/// <summary>
/// A key the agent enrolls with. Only its hash is kept; it may be limited in time and uses, put the new clients into a group and let them wait for an approval.
/// </summary>
public sealed class EnrollmentKey : SoftEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the key.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>The first characters, to recognise it in the list.</summary>
    public string Prefix { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime? ExpiresDate { get; set; }

    /// <summary>0 = no limit.</summary>
    public int MaxUses { get; set; }

    public int UseCount { get; set; }

    /// <summary>False: new clients wait in "Pending" for an approval.</summary>
    public bool AutoApprove { get; set; } = true;

    public long? ClientGroupId { get; set; }

    public ClientGroup? ClientGroup { get; set; }
}

// ==== Scripts =========================================================================================================================

public enum ScriptLanguage
{
    PowerShell = 1,
    Cmd = 2,
    Bash = 3,
}

public enum ScriptEditor
{
    /// <summary>Written as code.</summary>
    Code = 1,

    /// <summary>A list of blocks, one after the other; compiled to PowerShell.</summary>
    Blocks = 2,

    /// <summary>A flow of steps with branches; compiled to PowerShell.</summary>
    Flow = 3,
}

/// <summary>A script that runs on clients. Blocks and flows keep their model (<see cref="Model"/>) and the PowerShell compiled from it (<see cref="Content"/>).</summary>
public sealed class Script : SoftEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Category { get; set; }

    public ScriptLanguage Language { get; set; } = ScriptLanguage.PowerShell;

    public ScriptEditor Editor { get; set; } = ScriptEditor.Code;

    /// <summary>The code that runs (written, or compiled from <see cref="Model"/>).</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>The blocks or the flow as JSON (<c>Services/Scripts/ScriptModel</c>); null for code.</summary>
    public string? Model { get; set; }

    public int TimeoutSeconds { get; set; } = 600;
}

/// <summary>A file of a script (an XML, a configuration, a small installer): the agent downloads it into the folder the script runs in (<c>./name</c>).</summary>
public sealed class ScriptFile : Entity
{
    public long ScriptId { get; set; }

    public Script? Script { get; set; }

    public string Name { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;
}

// ==== Packages ========================================================================================================================

public enum PackageKind
{
    /// <summary>A package of the Windows Package Manager: installed and kept up to date by its id.</summary>
    Winget = 1,

    /// <summary>An MSI file uploaded to MatHub.</summary>
    Msi = 2,

    /// <summary>An installer (.exe) uploaded to MatHub, with its silent arguments.</summary>
    Exe = 3,

    /// <summary>Scripts for install and uninstall.</summary>
    Script = 4,
}

public enum DetectionKind
{
    /// <summary>Not checked: an assignment installs once per client.</summary>
    None = 1,

    /// <summary>The software list of the client has a program whose name contains the value.</summary>
    DisplayName = 2,

    /// <summary>The winget id is in the software list of the client.</summary>
    WingetId = 3,
}

/// <summary>
/// Software MatHub installs, updates and removes: the product ("7-Zip"). What runs is a <see cref="PackageVersion"/> with its installers per platform; only an
/// approved version is distributed (a version in test only to the pilot group). The detection says whether, and in which version, a client has it.
/// </summary>
public sealed class Package : SoftEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Publisher { get; set; }

    public string? Description { get; set; }

    /// <summary>The kind of the installer of the approved version (for the lists; the versions decide).</summary>
    public PackageKind Kind { get; set; } = PackageKind.Winget;

    /// <summary>The approved version (for the lists; kept by <c>PackageVersions</c>).</summary>
    public string? Version { get; set; }

    /// <summary>Its id in the catalog of winget: new versions are found there, and winget recognises it on the clients.</summary>
    public string? WingetId { get; set; }

    public DetectionKind Detection { get; set; } = DetectionKind.None;

    public string? DetectionValue { get; set; }

    /// <summary>MatHub prepares new versions of the catalog by itself (downloaded, checked, "new" – never approved by itself).</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>The group that gets a version in test before everybody.</summary>
    public long? PilotGroupId { get; set; }

    public ClientGroup? PilotGroup { get; set; }

    public ICollection<PackageAssignment> Assignments { get; set; } = [];

    public ICollection<PackageVersion> Versions { get; set; } = [];
}

public enum VersionStatus
{
    /// <summary>Prepared (uploaded, or found in the catalog): not distributed.</summary>
    New = 1,

    /// <summary>Distributed to the pilot group only.</summary>
    Testing = 2,

    /// <summary>Distributed to every assignment.</summary>
    Approved = 3,

    /// <summary>Replaced or withdrawn; kept for a way back.</summary>
    Retired = 4,
}

/// <summary>A version of a package: its installers per platform and its state of approval.</summary>
public sealed class PackageVersion : Entity
{
    public long PackageId { get; set; }

    public Package? Package { get; set; }

    public string Version { get; set; } = string.Empty;

    public VersionStatus Status { get; set; } = VersionStatus.New;

    /// <summary>upload, store, catalog, script, migrated.</summary>
    public string Source { get; set; } = "upload";

    public string? Notes { get; set; }

    public DateTime? ApprovedDate { get; set; }

    public long? ApprovedUserId { get; set; }

    public ICollection<PackageInstaller> Installers { get; set; } = [];
}

/// <summary>The platform an installer is for; neutral installers fit every computer.</summary>
public enum InstallerArchitecture
{
    Neutral = 0,
    X64 = 1,
    X86 = 2,
    Arm64 = 3,
}

/// <summary>
/// An installer of a version for one platform: a file kept in MatHub (MSI, EXE), a script, or winget with a pinned version. The agent takes the one that fits
/// its computer.
/// </summary>
public sealed class PackageInstaller : Entity
{
    public long PackageVersionId { get; set; }

    public PackageVersion? PackageVersion { get; set; }

    public InstallerArchitecture Architecture { get; set; } = InstallerArchitecture.Neutral;

    public PackageKind Kind { get; set; } = PackageKind.Msi;

    /// <summary>The file, in the data volume (<c>/data/packages/{package}/{installer}/</c>).</summary>
    public string? FileName { get; set; }

    public long? FileSize { get; set; }

    public string? FileSha256 { get; set; }

    /// <summary>MSI: added to /qn; EXE: the silent switches.</summary>
    public string? Arguments { get; set; }

    /// <summary>EXE: the command line that removes it; script: the PowerShell that removes it.</summary>
    public string? UninstallCommand { get; set; }

    /// <summary>Script: the PowerShell that installs it.</summary>
    public string? InstallScript { get; set; }

    /// <summary>winget: the id; with <see cref="WingetVersion"/> exactly that version (empty: the latest, only for packages from before the versions).</summary>
    public string? WingetId { get; set; }

    public string? WingetVersion { get; set; }

    /// <summary>MSI: removes it without the file (msiexec /x).</summary>
    public string? ProductCode { get; set; }

    /// <summary>Where it was downloaded from (the store).</summary>
    public string? SourceUrl { get; set; }
}

/// <summary>A program of the winget catalog (the store of MatHub): its latest version, imported every day from the source of Microsoft.</summary>
public sealed class CatalogPackage : Entity
{
    public string WingetId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Moniker { get; set; }

    public string Version { get; set; } = string.Empty;

    /// <summary>The publisher as winget normalises it (small letters): for the search.</summary>
    public string? Publisher { get; set; }

    /// <summary>The tags, separated by spaces.</summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>The path of its merged manifest on the CDN of winget.</summary>
    public string ManifestPath { get; set; } = string.Empty;
}

public enum AssignmentAction
{
    Install = 1,
    Uninstall = 2,
}

/// <summary>The wish that the clients of a group have a package (or have it not). MatHub plans the jobs that make it so (<c>Services/Deployments/Planner</c>).</summary>
public sealed class PackageAssignment : Entity
{
    public long PackageId { get; set; }

    public Package? Package { get; set; }

    public long ClientGroupId { get; set; }

    public ClientGroup? ClientGroup { get; set; }

    public AssignmentAction Action { get; set; } = AssignmentAction.Install;

    public bool IsActive { get; set; } = true;
}

// ==== Jobs ============================================================================================================================

public enum JobKind
{
    Script = 1,
    PackageInstall = 2,
    PackageUninstall = 3,

    /// <summary>winget upgrade of one id (or of everything when none is given).</summary>
    WingetUpgrade = 4,

    Inventory = 5,
    Restart = 6,

    /// <summary>A sequence: several steps one after the other (scripts, packages, restarts), each handed to the agent when the one before is done.</summary>
    Sequence = 7,
}

/// <summary>Work for clients: a script, an install, an update. Every client gets a <see cref="JobRun"/>; the job keeps what ran (a copy, so a later edit changes nothing).</summary>
public sealed class Job : Entity
{
    public string Name { get; set; } = string.Empty;

    public JobKind Kind { get; set; }

    public long? ScriptId { get; set; }

    public long? PackageId { get; set; }

    public long? PackageAssignmentId { get; set; }

    /// <summary>The version of the package the job installs (or removes).</summary>
    public long? PackageVersionId { get; set; }

    /// <summary>What runs: the script, or the winget id; a copy taken when the job was made.</summary>
    public string Payload { get; set; } = string.Empty;

    public ScriptLanguage Language { get; set; } = ScriptLanguage.PowerShell;

    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>Not before this time (UTC); null = at once.</summary>
    public DateTime? NotBeforeDate { get; set; }

    /// <summary>Who or what started it: a person (CreateUserId), the planner of the assignments, the updates, the kiosk of a client.</summary>
    public string Origin { get; set; } = "manual";

    public long? SequenceId { get; set; }

    public long? KioskItemId { get; set; }

    /// <summary>A sequence: its steps as JSON (<c>Services/Jobs/JobStep</c>), each with what the agent runs; a copy taken when the job was made.</summary>
    public string? Steps { get; set; }

    public ICollection<JobRun> Runs { get; set; } = [];
}

public enum RunStatus
{
    Queued = 1,

    /// <summary>The agent took it.</summary>
    Running = 2,

    Succeeded = 3,
    Failed = 4,
    Cancelled = 5,
}

/// <summary>A job on one client.</summary>
public sealed class JobRun : Entity
{
    public long JobId { get; set; }

    public Job? Job { get; set; }

    public long ClientId { get; set; }

    public Client? Client { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Queued;

    public DateTime? StartedDate { get; set; }

    public DateTime? FinishedDate { get; set; }

    public int? ExitCode { get; set; }

    /// <summary>What the script wrote (stdout and stderr), the end of it when it is long.</summary>
    public string? Output { get; set; }

    /// <summary>A sequence: the step that runs or comes next (0 = the first).</summary>
    public int Step { get; set; }

    /// <summary>When the agent took the current step (a sequence; else the start).</summary>
    public DateTime? StepDate { get; set; }

    /// <summary>A sequence after a restart: the next step waits until the client started again after this moment (or half an hour went by).</summary>
    public DateTime? WaitBootDate { get; set; }
}

// ==== Secrets, sequences, kiosk =======================================================================================================

/// <summary>
/// A value scripts need and nobody should read: the password of a domain join, a Wi-Fi key. Encrypted with DataProtection; the value is never shown again and
/// goes only to the agent, with the run whose script names it (<c>$env:MATHUB_SECRET_NAME</c>); the agent hides it in the output.
/// </summary>
public sealed class Secret : SoftEntity
{
    /// <summary>Capitals, digits and _ (it becomes the name of an environment variable).</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>A user name that belongs to it (the account of a domain join), shown and handed out as it is.</summary>
    public string? UserName { get; set; }

    public string ValueProtected { get; set; } = string.Empty;
}

public enum SequenceStepKind
{
    Script = 1,
    PackageInstall = 2,
    PackageUninstall = 3,

    /// <summary>Restarts the computer; the next step waits until it is back.</summary>
    Restart = 4,

    Inventory = 5,
    Wait = 6,
    WingetUpgrade = 7,
}

/// <summary>Scripts, packages and restarts one after the other, as one job (a "task sequence"): a computer set up in one go, a migration in steps.</summary>
public sealed class Sequence : SoftEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Category { get; set; }

    /// <summary>The steps as JSON (<c>Services/Jobs/SequenceStep</c>).</summary>
    public string Steps { get; set; } = "[]";
}

public enum KioskKind
{
    Script = 1,
    Package = 2,
    Sequence = 3,
}

/// <summary>
/// An offer in the kiosk of the clients: the people at a computer start it themselves (install a program, repair Outlook, map the drives). The agent asks the
/// server for the offers when its kiosk opens, so the list is always the current one.
/// </summary>
public sealed class KioskItem : SoftEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Category { get; set; }

    /// <summary>The icon of the tile (one of <c>Services/Kiosk/KioskService.Icons</c>).</summary>
    public string Icon { get; set; } = "box";

    public KioskKind Kind { get; set; } = KioskKind.Script;

    public long? ScriptId { get; set; }

    public long? PackageId { get; set; }

    public long? SequenceId { get; set; }

    /// <summary>The groups whose clients see it; empty = every client.</summary>
    public List<long> GroupIds { get; set; } = [];

    /// <summary>A question the person answers before it starts ("Outlook closes. Go on?"); empty = it starts at once.</summary>
    public string? Confirm { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }
}

// ==== Rollout (network boot) ==========================================================================================================

public enum BootImageKind
{
    /// <summary>Windows PE: boot.wim, BCD and boot.sdi, started through wimboot.</summary>
    WindowsPe = 1,

    /// <summary>A Linux kernel and its initrd.</summary>
    Linux = 2,
}

/// <summary>What a client boots over the network. The files lie in the data volume (<c>/data/netboot/images/{id}/</c>).</summary>
public sealed class BootImage : SoftEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public BootImageKind Kind { get; set; } = BootImageKind.WindowsPe;

    /// <summary>Linux: the kernel command line ({server}, {token} and {answer} are replaced).</summary>
    public string? KernelArguments { get; set; }
}

public enum ProfileKind
{
    /// <summary>Windows setup from an installation share, with an answer file MatHub writes.</summary>
    Windows = 1,

    /// <summary>A Linux installer with an answer file (preseed, kickstart, autoinstall).</summary>
    Linux = 2,

    /// <summary>An iPXE script of your own.</summary>
    Custom = 3,
}

/// <summary>How a computer is installed: what it boots, where the installation comes from and what it gets afterwards (agent, group).</summary>
public sealed class RolloutProfile : SoftEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ProfileKind Kind { get; set; } = ProfileKind.Windows;

    public long? BootImageId { get; set; }

    public BootImage? BootImage { get; set; }

    /// <summary>Windows: the share with the installation files (<c>\\server\share\win11</c>).</summary>
    public string? SourcePath { get; set; }

    public string? SourceUser { get; set; }

    /// <summary>The password of the share, encrypted (DataProtection).</summary>
    public string? SourcePasswordProtected { get; set; }

    /// <summary>Windows: the edition in install.wim (/IMAGE/INDEX).</summary>
    public int ImageIndex { get; set; } = 1;

    public string? ProductKey { get; set; }

    public string Locale { get; set; } = "de-DE";

    public string TimeZone { get; set; } = "W. Europe Standard Time";

    /// <summary>The password of the local administrator, encrypted (DataProtection).</summary>
    public string? AdminPasswordProtected { get; set; }

    /// <summary>Windows: an answer file of your own instead of the one MatHub writes; Linux: the answer file. Placeholders: {hostname}, {server}, {token} …</summary>
    public string? AnswerFile { get; set; }

    /// <summary>Custom: the iPXE script.</summary>
    public string? IpxeScript { get; set; }

    /// <summary>Install the agent after the setup (Windows) so the computer turns up as a client.</summary>
    public bool InstallAgent { get; set; } = true;

    /// <summary>The group the new client is put into.</summary>
    public long? ClientGroupId { get; set; }

    public ClientGroup? ClientGroup { get; set; }
}

/// <summary>A computer that booted from the network (seen by its MAC address), with what its firmware told iPXE.</summary>
public sealed class BootDevice : Entity
{
    /// <summary>Lower case with colons.</summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>efi or pcbios.</summary>
    public string? Platform { get; set; }

    public string? Manufacturer { get; set; }

    public string? Product { get; set; }

    public string? SerialNumber { get; set; }

    public string? Uuid { get; set; }

    public string? IpAddress { get; set; }

    public DateTime LastBootDate { get; set; }

    public int BootCount { get; set; }
}

public enum RolloutStatus
{
    /// <summary>Waits for the next network boot of the computer.</summary>
    Waiting = 1,

    /// <summary>It booted the installer; the next boots go to the disk.</summary>
    Installing = 2,

    /// <summary>The agent of the new installation enrolled.</summary>
    Completed = 3,

    Cancelled = 4,
}

/// <summary>The order to install one computer (by its MAC address) with a profile at its next network boot.</summary>
public sealed class RolloutTask : Entity
{
    /// <summary>The secret in the addresses of the answer file and of the agent install of this task.</summary>
    public Guid Token { get; set; } = Guid.NewGuid();

    public string MacAddress { get; set; } = string.Empty;

    /// <summary>The computer name the installation gets.</summary>
    public string HostName { get; set; } = string.Empty;

    public long ProfileId { get; set; }

    public RolloutProfile? Profile { get; set; }

    public RolloutStatus Status { get; set; } = RolloutStatus.Waiting;

    public DateTime? BootedDate { get; set; }

    public DateTime? CompletedDate { get; set; }

    /// <summary>The client the installation became.</summary>
    public long? ClientId { get; set; }
}

// ==== Instance ========================================================================================================================

public sealed class Activity : Entity
{
    public long? UserId { get; set; }

    public long? ClientId { get; set; }

    /// <summary>client, group, script, package, job, rollout, role, user, settings …</summary>
    public string Area { get; set; } = string.Empty;

    /// <summary>create, update, delete, enroll, approve, run, cancel …</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>The name of the item at the time (it may be gone later).</summary>
    public string? Target { get; set; }

    public string? Detail { get; set; }

    public DateTime OccurredDate { get; set; }
}

public sealed class SystemSetting : Entity
{
    /// <summary>Whether anyone may create an account (the first account of an installation can always sign up and becomes the administrator).</summary>
    public bool RegistrationOpen { get; set; }

    /// <summary>The address the app is reached at from outside (links in mails, the agent install); empty = the address of the request.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>How often the agents call (seconds).</summary>
    public int AgentPollSeconds { get; set; } = 30;

    /// <summary>How often the agents send their inventory (hours).</summary>
    public int InventoryHours { get; set; } = 6;

    /// <summary>Jobs and their runs are removed after these days.</summary>
    public int JobDays { get; set; } = 90;

    /// <summary>
    /// The address network-booting computers load the boot files from (<c>http://10.0.0.5:4910</c>); empty = the TFTP server's address
    /// (<c>${next-server}</c>) with the port of MatHub.
    /// </summary>
    public string? BootUrl { get; set; }
}
