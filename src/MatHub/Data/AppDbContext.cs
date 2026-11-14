using MatHub.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MatHub.Data;

/// <summary>
/// The database (PostgreSQL). Tables are PascalCase and singular, the key is always <c>Id</c> (BIGINT), every record has CreateDate, CreateUserId, UpdateDate and UpdateUserId
/// (filled in <see cref="SaveChangesAsync(bool, CancellationToken)"/>), and a record with an <c>UpdateState</c> is deleted softly (0 = deleted, 1 = created, 2 = updated).
/// The installers and the boot files are not in the database: they lie in the data volume (<c>/data/packages</c>, <c>/data/netboot</c>).
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, CurrentUser? current = null) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<UserSession> Sessions => Set<UserSession>();

    public DbSet<AccessRole> AccessRoles => Set<AccessRole>();

    public DbSet<AccessRoleGroup> AccessRoleGroups => Set<AccessRoleGroup>();

    public DbSet<Client> Clients => Set<Client>();

    public DbSet<ClientSoftware> ClientSoftware => Set<ClientSoftware>();

    public DbSet<ClientGroup> ClientGroups => Set<ClientGroup>();

    public DbSet<ClientGroupMember> ClientGroupMembers => Set<ClientGroupMember>();

    public DbSet<EnrollmentKey> EnrollmentKeys => Set<EnrollmentKey>();

    public DbSet<Script> Scripts => Set<Script>();

    public DbSet<Package> Packages => Set<Package>();

    public DbSet<PackageAssignment> PackageAssignments => Set<PackageAssignment>();

    public DbSet<ClientPackage> ClientPackages => Set<ClientPackage>();

    public DbSet<PackageVersion> PackageVersions => Set<PackageVersion>();

    public DbSet<PackageInstaller> PackageInstallers => Set<PackageInstaller>();

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<JobRun> JobRuns => Set<JobRun>();

    public DbSet<BootImage> BootImages => Set<BootImage>();

    public DbSet<RolloutProfile> RolloutProfiles => Set<RolloutProfile>();

    public DbSet<BootDevice> BootDevices => Set<BootDevice>();

    public DbSet<RolloutTask> RolloutTasks => Set<RolloutTask>();

    public DbSet<ScriptFile> ScriptFiles => Set<ScriptFile>();

    public DbSet<CatalogPackage> CatalogPackages => Set<CatalogPackage>();

    public DbSet<Secret> Secrets => Set<Secret>();

    public DbSet<Sequence> Sequences => Set<Sequence>();

    public DbSet<KioskItem> KioskItems => Set<KioskItem>();

    public DbSet<Activity> Activities => Set<Activity>();

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("User");
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.Language).HasMaxLength(5);
            e.Property(x => x.Theme).HasMaxLength(10);
            e.Property(x => x.Scheme).HasMaxLength(10);
            e.Property(x => x.IdentityPermissions).HasMaxLength(2000);
            e.Property(x => x.ExternalSubject).HasMaxLength(200);
            e.HasIndex(x => x.ExternalSubject).IsUnique().HasFilter("\"ExternalSubject\" IS NOT NULL");
            e.HasOne(x => x.AccessRole).WithMany().HasForeignKey(x => x.AccessRoleId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<UserSession>(e =>
        {
            e.ToTable("UserSession");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.UserAgent).HasMaxLength(300);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.IdentitySessionId).HasMaxLength(200);
            e.HasIndex(x => x.IdentitySessionId);
        });

        b.Entity<AccessRole>(e =>
        {
            e.ToTable("AccessRole");
            e.Property(x => x.Key).HasMaxLength(40);
            e.HasIndex(x => x.Key).IsUnique().HasFilter("\"Key\" IS NOT NULL");
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Permissions).HasMaxLength(2000);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<AccessRoleGroup>(e =>
        {
            e.ToTable("AccessRoleGroup");
            e.HasIndex(x => new { x.AccessRoleId, x.ClientGroupId }).IsUnique();
            e.HasOne(x => x.AccessRole).WithMany(x => x.Groups).HasForeignKey(x => x.AccessRoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ClientGroup).WithMany().HasForeignKey(x => x.ClientGroupId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Client>(e =>
        {
            e.ToTable("Client");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.MachineId);
            e.Property(x => x.SecretHash).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Tags).HasMaxLength(500);
            e.Property(x => x.MachineId).HasMaxLength(64);
            e.Property(x => x.Domain).HasMaxLength(200);
            e.Property(x => x.OperatingSystem).HasMaxLength(200);
            e.Property(x => x.OsVersion).HasMaxLength(60);
            e.Property(x => x.OsBuild).HasMaxLength(60);
            e.Property(x => x.Architecture).HasMaxLength(30);
            e.Property(x => x.Manufacturer).HasMaxLength(200);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.SerialNumber).HasMaxLength(120);
            e.Property(x => x.Processor).HasMaxLength(200);
            e.Property(x => x.IpAddresses).HasMaxLength(1000);
            e.Property(x => x.MacAddresses).HasMaxLength(1000);
            e.Property(x => x.LastUser).HasMaxLength(200);
            e.Property(x => x.AgentVersion).HasMaxLength(60);
            e.Property(x => x.Inventory).HasColumnType("jsonb");
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<ClientSoftware>(e =>
        {
            e.ToTable("ClientSoftware");
            e.HasIndex(x => x.ClientId);
            e.HasIndex(x => x.WingetId);
            e.HasOne(x => x.Client).WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Version).HasMaxLength(100);
            e.Property(x => x.Publisher).HasMaxLength(300);
            e.Property(x => x.WingetId).HasMaxLength(200);
            e.Property(x => x.AvailableVersion).HasMaxLength(100);
        });

        b.Entity<ClientGroup>(e =>
        {
            e.ToTable("ClientGroup");
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Colour).HasMaxLength(7);
            e.Property(x => x.RuleValue).HasMaxLength(200);
            e.Property(x => x.MoreRules).HasColumnType("jsonb");
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<ClientGroupMember>(e =>
        {
            e.ToTable("ClientGroupMember");
            e.HasIndex(x => new { x.ClientGroupId, x.ClientId }).IsUnique();
            e.HasIndex(x => x.ClientId);
            e.HasOne(x => x.ClientGroup).WithMany(x => x.Members).HasForeignKey(x => x.ClientGroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Client).WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EnrollmentKey>(e =>
        {
            e.ToTable("EnrollmentKey");
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.KeyHash).HasMaxLength(64);
            e.Property(x => x.Prefix).HasMaxLength(16);
            e.HasOne(x => x.ClientGroup).WithMany().HasForeignKey(x => x.ClientGroupId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<Script>(e =>
        {
            e.ToTable("Script");
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Category).HasMaxLength(60);
            e.Property(x => x.Model).HasColumnType("jsonb");
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<Package>(e =>
        {
            e.ToTable("Package");
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Publisher).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Version).HasMaxLength(100);
            e.Property(x => x.WingetId).HasMaxLength(200);
            e.Property(x => x.DetectionValue).HasMaxLength(300);
            e.HasOne(x => x.PilotGroup).WithMany().HasForeignKey(x => x.PilotGroupId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<PackageVersion>(e =>
        {
            e.ToTable("PackageVersion");
            e.Property(x => x.Version).HasMaxLength(100);
            e.Property(x => x.Source).HasMaxLength(20);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasIndex(x => new { x.PackageId, x.Status });
            e.HasOne(x => x.Package).WithMany(x => x.Versions).HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PackageInstaller>(e =>
        {
            e.ToTable("PackageInstaller");
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.FileSha256).HasMaxLength(64);
            e.Property(x => x.Arguments).HasMaxLength(1000);
            e.Property(x => x.UninstallCommand).HasMaxLength(4000);
            e.Property(x => x.WingetId).HasMaxLength(200);
            e.Property(x => x.WingetVersion).HasMaxLength(100);
            e.Property(x => x.ProductCode).HasMaxLength(60);
            e.Property(x => x.SourceUrl).HasMaxLength(1000);
            e.HasOne(x => x.PackageVersion).WithMany(x => x.Installers).HasForeignKey(x => x.PackageVersionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PackageAssignment>(e =>
        {
            e.ToTable("PackageAssignment");
            e.HasIndex(x => new { x.PackageId, x.ClientGroupId }).IsUnique();
            e.HasOne(x => x.Package).WithMany(x => x.Assignments).HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ClientGroup).WithMany().HasForeignKey(x => x.ClientGroupId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ClientPackage>(e =>
        {
            e.ToTable("ClientPackage");
            e.HasIndex(x => new { x.PackageId, x.ClientId }).IsUnique();
            e.HasIndex(x => x.ClientId);
            e.HasOne(x => x.Client).WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Package).WithMany().HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.PackageVersion).WithMany().HasForeignKey(x => x.PackageVersionId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Job>(e =>
        {
            e.ToTable("Job");
            e.HasIndex(x => x.CreateDate);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Origin).HasMaxLength(20);
            e.Property(x => x.Steps).HasColumnType("jsonb");
        });

        b.Entity<CatalogPackage>(e =>
        {
            e.ToTable("CatalogPackage");
            e.Property(x => x.WingetId).HasMaxLength(200);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Moniker).HasMaxLength(100);
            e.Property(x => x.Version).HasMaxLength(100);
            e.Property(x => x.Publisher).HasMaxLength(200);
            e.Property(x => x.Tags).HasMaxLength(2000);
            e.Property(x => x.ManifestPath).HasMaxLength(400);
            e.HasIndex(x => x.WingetId).IsUnique();
            e.HasIndex(x => x.Name);
        });

        b.Entity<ScriptFile>(e =>
        {
            e.ToTable("ScriptFile");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.HasIndex(x => new { x.ScriptId, x.Name }).IsUnique();
            e.HasOne(x => x.Script).WithMany().HasForeignKey(x => x.ScriptId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Secret>(e =>
        {
            e.ToTable("Secret");
            e.Property(x => x.Name).HasMaxLength(60);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.UserName).HasMaxLength(200);
            e.Property(x => x.ValueProtected).HasMaxLength(4000);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<Sequence>(e =>
        {
            e.ToTable("Sequence");
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Category).HasMaxLength(60);
            e.Property(x => x.Steps).HasColumnType("jsonb");
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<KioskItem>(e =>
        {
            e.ToTable("KioskItem");
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Category).HasMaxLength(60);
            e.Property(x => x.Icon).HasMaxLength(40);
            e.Property(x => x.Confirm).HasMaxLength(500);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<JobRun>(e =>
        {
            e.ToTable("JobRun");
            e.HasIndex(x => new { x.ClientId, x.Status });
            e.HasIndex(x => x.JobId);
            e.HasOne(x => x.Job).WithMany(x => x.Runs).HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Client).WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BootImage>(e =>
        {
            e.ToTable("BootImage");
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.KernelArguments).HasMaxLength(2000);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<RolloutProfile>(e =>
        {
            e.ToTable("RolloutProfile");
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.SourcePath).HasMaxLength(500);
            e.Property(x => x.SourceUser).HasMaxLength(200);
            e.Property(x => x.SourcePasswordProtected).HasMaxLength(2000);
            e.Property(x => x.ProductKey).HasMaxLength(40);
            e.Property(x => x.Locale).HasMaxLength(20);
            e.Property(x => x.TimeZone).HasMaxLength(100);
            e.Property(x => x.AdminPasswordProtected).HasMaxLength(2000);
            e.HasOne(x => x.BootImage).WithMany().HasForeignKey(x => x.BootImageId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.ClientGroup).WithMany().HasForeignKey(x => x.ClientGroupId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<BootDevice>(e =>
        {
            e.ToTable("BootDevice");
            e.HasIndex(x => x.MacAddress).IsUnique();
            e.Property(x => x.MacAddress).HasMaxLength(17);
            e.Property(x => x.Platform).HasMaxLength(20);
            e.Property(x => x.Manufacturer).HasMaxLength(200);
            e.Property(x => x.Product).HasMaxLength(200);
            e.Property(x => x.SerialNumber).HasMaxLength(120);
            e.Property(x => x.Uuid).HasMaxLength(60);
            e.Property(x => x.IpAddress).HasMaxLength(64);
        });

        b.Entity<RolloutTask>(e =>
        {
            e.ToTable("RolloutTask");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.MacAddress);
            e.Property(x => x.MacAddress).HasMaxLength(17);
            e.Property(x => x.HostName).HasMaxLength(63);
            e.HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Activity>(e =>
        {
            e.ToTable("Activity");
            e.HasIndex(x => x.OccurredDate);
            e.HasIndex(x => new { x.ClientId, x.OccurredDate });
            e.Property(x => x.Area).HasMaxLength(20);
            e.Property(x => x.Action).HasMaxLength(30);
            e.Property(x => x.Target).HasMaxLength(255);
            e.Property(x => x.Detail).HasMaxLength(1000);
        });

        b.Entity<SystemSetting>(e =>
        {
            e.ToTable("SystemSetting");
            e.Property(x => x.PublicUrl).HasMaxLength(300);
            e.Property(x => x.BootUrl).HasMaxLength(300);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Fills the audit columns, and turns the delete of a soft record into UpdateState 0.</summary>
    private void Stamp()
    {
        var now = DateTime.UtcNow;
        var user = current?.Id;
        foreach (var entry in ChangeTracker.Entries<Entity>().ToList())
        {
            var entity = entry.Entity;
            if (entry.State == EntityState.Deleted && entity is SoftEntity soft)
            {
                entry.State = EntityState.Modified;
                soft.UpdateState = SoftEntity.Deleted;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (entity.CreateDate == default)
                    {
                        entity.CreateDate = now;
                    }

                    entity.CreateUserId ??= user;
                    entity.UpdateDate = now;
                    entity.UpdateUserId = user ?? entity.CreateUserId;
                    break;
                case EntityState.Modified:
                    entity.UpdateDate = now;
                    entity.UpdateUserId = user ?? entity.UpdateUserId;
                    if (entity is SoftEntity { UpdateState: not SoftEntity.Deleted } updated)
                    {
                        updated.UpdateState = SoftEntity.Updated;
                    }

                    break;
            }
        }
    }
}

/// <summary>For <c>dotnet ef migrations add</c>: no database is needed to create a migration.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=mathub;Username=mathub;Password=mathub").Options);
}
