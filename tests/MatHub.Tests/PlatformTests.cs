using System.Net;
using System.Text.Json;
using MatHub.Platform;
using MatHub.Services.Access;
using MatPlatform;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Tests;

/// <summary>The description of MatHub for the platform (contract 2): valid, what it consumes, its launcher and widgets, its roles and permissions, the fixed public address.</summary>
public sealed class PlatformTests
{
    private static readonly string Web = Path.Combine(Root(), "src", "MatHub", "wwwroot");

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_definition_is_valid_with_the_icons_of_the_MatUi_sprite(string language)
    {
        var sprite = MatPlatformValidator.ReadSprite(Path.Combine(Web, "matui", "matui-icons.svg"));
        Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(MatHubPlatform.Describe, language), sprite));
        Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(o => MatHubPlatform.Describe(o, "https://hub.example.com"), language), sprite));
    }

    [Fact]
    public void MatHub_consumes_the_sign_in_and_the_mail_and_contributes_a_launcher_and_widgets()
    {
        var definition = MatPlatformBuilder.Build(MatHubPlatform.Describe, "en");
        Assert.Equal("mathub", definition.App.Id);
        Assert.Contains(definition.Consumes!, c => c.Capability == MatCapabilities.IdentityOidc && c.Optional == true);
        Assert.Contains(definition.Consumes!, c => c.Capability == MatCapabilities.MailSmtp && c.Optional == true);
        var launcher = Assert.Single(definition.Contributes!.Launchers);
        Assert.Equal("hub", launcher.Id);
        Assert.Equal(4, definition.Contributes.QuickActions.Count(q => q.Launcher == "hub"));
        Assert.All(definition.Contributes.Widgets, w => Assert.Equal(MatAudience.Admin, w.Audience));
    }

    [Fact]
    public void Every_permission_of_MatHub_is_in_the_definition_with_the_built_in_roles()
    {
        var definition = MatPlatformBuilder.Build(MatHubPlatform.Describe, "en");
        Assert.Equal(Permission.All.Order(), definition.Permissions!.Select(p => p.Key).Order());
        Assert.Equal(["admin", RoleService.Technician, RoleService.Operator, RoleService.Viewer], definition.Roles!.Select(r => r.Key));
        Assert.All(definition.Permissions!, p => Assert.Contains(RoleService.Technician, p.Roles!));
    }

    [Fact]
    public void MAT_PUBLIC_URL_fixes_the_public_address()
    {
        Assert.Null(MatPlatformBuilder.Build(MatHubPlatform.Describe, "en").Configure.Single(f => f.Key == "publicUrl").Fixed);
        var field = MatPlatformBuilder.Build(o => MatHubPlatform.Describe(o, "https://hub.example.com"), "en").Configure.Single(f => f.Key == "publicUrl");
        Assert.True(field.Fixed);
    }

    internal static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatHub.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("MatHub.slnx not found.");
    }
}

/// <summary>MatHub in memory with the variables of a broker: the platform endpoints with a key, the fixed public address, the health.</summary>
public sealed class PlatformAppTests : IAsyncLifetime
{
    private const string ApiKey = "test-api-key-0123456789";
    private TestDatabase? _database;
    private WebApplicationFactory<Program>? _factory;
    private readonly string _data = Path.Combine(Path.GetTempPath(), "mathub-tests", Guid.NewGuid().ToString("N")[..12]);

    public async Task InitializeAsync()
    {
        if (TestDatabase.Admin is null)
        {
            return;
        }

        _database = await TestDatabase.CreateAsync();
        Directory.CreateDirectory(_data);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("ConnectionStrings:Default", _database.Db.Database.GetConnectionString());
            web.UseSetting("DataDirectory", _data);
            web.UseSetting("Netboot:Tftp", "off");
            web.UseSetting("Logging:LogLevel:Default", "Warning");
            web.UseSetting("MAT_API_KEY", ApiKey);
            web.UseSetting("MAT_PUBLIC_URL", "https://hub.example.com/");
        });
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private HttpClient Api()
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        return client;
    }

    [DbFact]
    public async Task The_platform_answers_with_the_key_and_fixes_the_public_address()
    {
        var api = Api();
        var definition = JsonDocument.Parse(await api.GetStringAsync("/api")).RootElement;
        Assert.Equal("mathub", definition.GetProperty("app").GetProperty("id").GetString());
        var publicUrl = definition.GetProperty("configure").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "publicUrl");
        Assert.True(publicUrl.GetProperty("fixed").GetBoolean());

        var state = JsonDocument.Parse(await api.GetStringAsync("/api/state")).RootElement;
        Assert.True(state.TryGetProperty("values", out _) || state.ValueKind == JsonValueKind.Object);

        // the health answers 200 also while it is degraded (no agent to hand out in a test build)
        var health = await api.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        // without the key: no state
        var anonymous = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/state")).StatusCode);
    }

    [DbFact]
    public async Task The_start_page_and_the_crawlers_files_are_public_and_the_rest_is_not()
    {
        var anonymous = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/robots.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/llms.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/manifest.webmanifest")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/clients")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/agent/v1/poll", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
    }
}
