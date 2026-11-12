using MatHub.Data;
using MatHub.Services.Scripts;

namespace MatHub.Tests;

/// <summary>The compiler of the visual editors: what a person types is data in the PowerShell, never code; blocks and flows compile, and say what is missing.</summary>
public sealed class ScriptTests
{
    private static ScriptBlock Block(string type, params (string Key, string Value)[] values) => new() { Type = type, Values = values.ToDictionary(v => v.Key, v => v.Value) };

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("$(Remove-Item C:\\)", "'$(Remove-Item C:\\)'")]
    [InlineData("‘typo’", "'‘‘typo’’'")]
    public void A_value_is_a_single_quoted_string(string value, string quoted) => Assert.Equal(quoted, ScriptBlocks.Quote(value));

    [Fact]
    public void Blocks_become_powershell_in_their_order()
    {
        var model = new ScriptModel
        {
            Blocks =
            [
                Block("folder", ("path", @"C:\Company")),
                Block("registry", ("path", @"HKLM:\SOFTWARE\Company"), ("name", "Ring"), ("type", "DWord"), ("value", "2")),
                Block("winget-install", ("id", "Mozilla.Firefox")),
            ],
        };
        var result = ScriptBlocks.CompileBlocks(model);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        var folder = result.Code.IndexOf(@"New-Item -ItemType Directory -Force -Path 'C:\Company'", StringComparison.Ordinal);
        var registry = result.Code.IndexOf("-PropertyType DWord -Value 2", StringComparison.Ordinal);
        var winget = result.Code.IndexOf("Invoke-MatHubWinget install --id 'Mozilla.Firefox'", StringComparison.Ordinal);
        Assert.True(folder > 0 && registry > folder && winget > registry);
        Assert.Contains("$ErrorActionPreference = 'Stop'", result.Code);
    }

    [Fact]
    public void A_value_cannot_break_out_of_its_string()
    {
        var result = ScriptBlocks.CompileBlocks(new ScriptModel { Blocks = [Block("message", ("text", "'; Remove-Item -Recurse C:\\ ; '"))] });
        Assert.Contains("Write-Output '''; Remove-Item -Recurse C:\\ ; '''", result.Code);
    }

    [Fact]
    public void A_condition_and_go_on_after_an_error_wrap_the_step()
    {
        var block = Block("service", ("name", "Spooler"), ("action", "restart")) with
        {
            When = new BlockCondition { Type = "service-running", Values = new() { ["name"] = "Spooler" }, Negate = true },
            ContinueOnError = true,
        };
        var code = ScriptBlocks.CompileBlocks(new ScriptModel { Blocks = [block] }).Code;
        Assert.Contains("if (-not ((Get-Service -Name 'Spooler' -ErrorAction SilentlyContinue).Status -eq 'Running'))", code);
        Assert.Contains("try {", code);
        Assert.Contains("Restart-Service -Name 'Spooler' -Force", code);
    }

    [Fact]
    public void Missing_values_and_wrong_numbers_are_named_with_their_step()
    {
        var result = ScriptBlocks.CompileBlocks(new ScriptModel { Blocks = [Block("winget-install"), Block("wait", ("seconds", "soon"))] });
        Assert.False(result.Ok);
        Assert.Equal(2, result.Errors.Count);
        Assert.False(ScriptBlocks.CompileBlocks(new ScriptModel()).Ok);
        Assert.False(ScriptBlocks.CompileBlocks(new ScriptModel { Blocks = [Block("no-such-block")] }).Ok);
    }

    [Fact]
    public void A_flow_is_a_state_machine_with_its_branches()
    {
        var model = new ScriptModel
        {
            Nodes =
            [
                new FlowNode { Id = "start", Type = "start" },
                new FlowNode { Id = "check", Type = "if", Condition = new BlockCondition { Type = "file-exists", Values = new() { ["path"] = @"C:\done.txt" } } },
                new FlowNode { Id = "install", Type = "winget-install", Values = new() { ["id"] = "7zip.7zip" } },
                new FlowNode { Id = "fail", Type = "end", Values = new() { ["code"] = "3" } },
                new FlowNode { Id = "end", Type = "end" },
            ],
            Edges =
            [
                new FlowEdge { From = "start", To = "check" },
                new FlowEdge { From = "check", To = "end", Port = "true" },
                new FlowEdge { From = "check", To = "install", Port = "false" },
                new FlowEdge { From = "install", To = "end" },
                new FlowEdge { From = "install", To = "fail", Port = "error" },
            ],
        };
        var result = ScriptBlocks.CompileFlow(model);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Contains("$step = 'check'", result.Code);
        Assert.Contains(@"if ((Test-Path -LiteralPath 'C:\done.txt')) { $step = 'end' } else { $step = 'install' }", result.Code);
        Assert.Contains("catch {", result.Code);
        Assert.Contains("$step = 'fail'", result.Code);
        Assert.Contains("exit 3", result.Code);
        Assert.Contains("10000", result.Code);
    }

    [Fact]
    public void A_flow_needs_one_start_that_leads_somewhere()
    {
        Assert.False(ScriptBlocks.CompileFlow(new ScriptModel()).Ok);
        Assert.False(ScriptBlocks.CompileFlow(new ScriptModel { Nodes = [new FlowNode { Id = "start", Type = "start" }] }).Ok);
    }

    [Fact]
    public void The_model_survives_a_round_trip_and_a_broken_one_is_empty()
    {
        var model = new ScriptModel { Blocks = [Block("wait", ("seconds", "5"))] };
        var back = ScriptBlocks.Parse(ScriptBlocks.Serialize(model));
        Assert.Equal("wait", Assert.Single(back.Blocks).Type);
        Assert.Empty(ScriptBlocks.Parse("{ not json").Blocks);
        Assert.Equal(ScriptEditor.Flow, ScriptEditor.Flow);
    }

    [Fact]
    public void A_variable_in_a_value_is_read_from_the_variables_and_nothing_else()
    {
        Assert.Equal("('BER-' + [string]$MatHubVars['serial'])", ScriptBlocks.Expression("BER-{{serial}}"));
        Assert.Equal("([string]$MatHubVars['answer'])", ScriptBlocks.Expression("{{ answer }}"));

        // what is not a name stays text
        Assert.Equal("'{{a''b}}'", ScriptBlocks.Expression("{{a'b}}"));
        Assert.Equal("'{{$(calc)}}'", ScriptBlocks.Expression("{{$(calc)}}"));
        var result = ScriptBlocks.CompileBlocks(new ScriptModel { Blocks = [Block("set-variable", ("variable", "bad name"), ("value", "x"))] });
        Assert.False(result.Ok);
    }

    [Fact]
    public void A_file_of_the_script_and_the_variables_of_windows_are_found_on_the_computer()
    {
        Assert.Equal("(Join-Path $MatHubFiles 'settings.xml')", ScriptBlocks.Expression("./settings.xml"));
        Assert.Equal(@"([Environment]::ExpandEnvironmentVariables('%ProgramData%\Contoso\settings.xml'))", ScriptBlocks.Expression(@"%ProgramData%\Contoso\settings.xml"));
        Assert.Equal(@"([Environment]::ExpandEnvironmentVariables(('%ProgramData%\' + [string]$MatHubVars['dept'])))", ScriptBlocks.Expression(@"%ProgramData%\{{dept}}"));
        Assert.Equal("'100%'", ScriptBlocks.Expression("100%"));

        var result = ScriptBlocks.CompileBlocks(new ScriptModel { Blocks = [Block("copy", ("source", "./settings.xml"), ("target", @"%ProgramData%\Contoso\"))] });
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Contains(@"Copy-Item -Path (Join-Path $MatHubFiles 'settings.xml') -Destination ([Environment]::ExpandEnvironmentVariables('%ProgramData%\Contoso\'))", result.Code);
        Assert.Contains("$MatHubFiles = if ($env:MATHUB_FILES)", result.Code);
    }

    [Fact]
    public void A_domain_join_names_its_secret_and_never_holds_the_password()
    {
        var result = ScriptBlocks.CompileBlocks(new ScriptModel
        {
            Blocks = [Block("domain-join", ("domain", "contoso.local"), ("credential", "DOMAIN_JOIN"), ("ou", "OU=Clients,DC=contoso,DC=local"), ("reboot", ""))],
        });
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Contains("New-MatHubCredential 'DOMAIN_JOIN'", result.Code);
        Assert.Contains("$join['OUPath'] = 'OU=Clients,DC=contoso,DC=local'", result.Code);
        Assert.DoesNotContain("shutdown.exe /r /t 30", result.Code);
        Assert.Equal(["DOMAIN_JOIN"], ScriptBlocks.SecretsIn(result.Code).Order());
        Assert.Contains("ADMIN", ScriptBlocks.SecretsIn("$env:MATHUB_SECRET_ADMIN_USER"));

        // a secret has to be chosen, with a proper name
        Assert.False(ScriptBlocks.CompileBlocks(new ScriptModel { Blocks = [Block("domain-join", ("domain", "contoso.local"), ("credential", "x'; calc"))] }).Ok);
    }

    [Fact]
    public void A_wizard_becomes_a_dialog_with_its_pages_and_fields()
    {
        var fields = """
            [{"kind":"page","label":"Welcome","default":"This sets up your computer."},
             {"kind":"text","name":"computerName","label":"Name of the computer","required":true},
             {"kind":"page","label":"Department"},
             {"kind":"choice","name":"department","label":"Department","options":"Sales; Engineering;Office"},
             {"kind":"check","name":"office","label":"Install Office","default":"true"}]
            """;
        var result = ScriptBlocks.CompileBlocks(new ScriptModel { Blocks = [Block("ui-form", ("title", "Set-up of {{computer}}"), ("fields", fields))] });
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        var line = result.Code.Split('\n').Single(l => l.StartsWith("Invoke-MatHubDialog '", StringComparison.Ordinal));
        var json = line["Invoke-MatHubDialog '".Length..^1].Replace("''", "'", StringComparison.Ordinal);
        var dialog = System.Text.Json.JsonSerializer.Deserialize<MatHub.Protocol.UiDialog>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal("form", dialog.Kind);
        Assert.Equal("Set-up of {{computer}}", dialog.Title);
        Assert.Equal(["Welcome", "Department"], dialog.Pages.Select(p => p.Title));
        Assert.Equal(["Sales", "Engineering", "Office"], dialog.Pages[1].Fields[0].Options!);
        Assert.True(dialog.Pages[0].Fields[0].Required);

        // two fields with one name, a choice without options: the person is told
        var wrong = """[{"kind":"text","name":"a","label":"A"},{"kind":"text","name":"a","label":"B"},{"kind":"choice","name":"c","label":"C"}]""";
        Assert.Equal(2, ScriptBlocks.CompileBlocks(new ScriptModel { Blocks = [Block("ui-form", ("title", "T"), ("fields", wrong))] }).Errors.Count);
    }

    [Fact]
    public void Every_action_and_every_check_compiles_with_its_fields_filled()
    {
        static string Sample(BlockField f) => f.Kind switch
        {
            "number" => "30",
            "select" => f.Options![0],
            "bool" => "true",
            "secret" => "SAMPLE_SECRET",
            "variable" => "sample",
            "fields" => """[{"kind":"text","name":"a","label":"A"}]""",
            _ => f.Name == "time" ? "03:00" : "Sample {{computer}}",
        };

        foreach (var type in ScriptBlocks.Types)
        {
            var values = type.Fields.ToDictionary(f => f.Name, Sample);
            var model = type.IsCondition
                ? new ScriptModel { Blocks = [new ScriptBlock { Type = "message", Values = new() { ["text"] = "x" }, When = new BlockCondition { Type = type.Id, Values = values } }] }
                : new ScriptModel { Blocks = [new ScriptBlock { Type = type.Id, Values = values }] };
            var result = ScriptBlocks.CompileBlocks(model);
            Assert.True(result.Ok, type.Id + ": " + string.Join("; ", result.Errors));
            Assert.True(ScriptBlocks.CompileFlow(ScriptBlocks.BlocksToFlow(model)).Ok, type.Id + " as a flow");
        }
    }

    [Fact]
    public void Blocks_become_a_flow_that_does_the_same()
    {
        var blocks = new ScriptModel
        {
            Blocks =
            [
                Block("folder", ("path", @"C:\Company")),
                new ScriptBlock { Type = "winget-install", Values = new() { ["id"] = "Mozilla.Firefox" }, When = new BlockCondition { Type = "program-installed", Values = new() { ["name"] = "Firefox" }, Negate = true } },
                new ScriptBlock { Type = "message", Values = new() { ["text"] = "done" }, ContinueOnError = true },
            ],
        };
        var flow = ScriptBlocks.BlocksToFlow(blocks);
        Assert.Equal(["start", "folder", "if", "winget-install", "message", "end"], flow.Nodes.Select(n => n.Type));
        Assert.Contains(flow.Edges, e => e.From == "if2" && e.Port == "false" && e.To == "s3");
        Assert.Contains(flow.Edges, e => e.From == "s3" && e.Port == "error" && e.To == "end");
        var result = ScriptBlocks.CompileFlow(flow);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Contains("Invoke-MatHubWinget install --id 'Mozilla.Firefox'", result.Code);
    }

    [Fact]
    public void A_trial_run_says_what_it_would_do_and_does_only_what_changes_nothing()
    {
        var model = new ScriptModel
        {
            Blocks =
            [
                Block("set-variable", ("variable", "ou"), ("value", "Sales")),
                Block("domain-join", ("domain", "contoso.local"), ("credential", "DOMAIN_JOIN"), ("ou", "OU={{ou}},DC=contoso,DC=local")),
                Block("powershell", ("code", "Remove-Item C:\\Data -Recurse\nWrite-Output 2")),
                Block("ui-question", ("title", "T"), ("text", "Go on?"), ("variable", "go")),
            ],
        };
        var result = ScriptBlocks.CompileBlocks(model, dryRun: true);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.DoesNotContain("Add-Computer", result.Code);
        Assert.DoesNotContain("New-MatHubCredential 'DOMAIN_JOIN'", result.Code);
        Assert.DoesNotContain("\nRemove-Item", result.Code);
        Assert.Contains("$MatHubVars['ou'] = 'Sales'", result.Code);
        Assert.Contains("[string]$MatHubVars['ou']", result.Code);
        Assert.Contains("Invoke-MatHubDialog", result.Code);
        Assert.Contains("$MatHubDryRun = $true", result.Code);
        Assert.True(ScriptBlocks.CompileFlow(ScriptBlocks.BlocksToFlow(model), dryRun: true).Ok);

        // code is only read: it is a string for the parser of PowerShell
        var code = ScriptTester.DryRunCode(ScriptLanguage.PowerShell, "Remove-Item C:\\Data -Recurse");
        Assert.Contains("$code = 'Remove-Item", code);
        Assert.Contains("Parser]::ParseInput", code);
        Assert.DoesNotContain("\nRemove-Item", code);
        Assert.Contains("del /q x", ScriptTester.DryRunCode(ScriptLanguage.Cmd, "@echo off\ndel /q x"));
    }

    [Fact]
    public void Every_block_has_its_texts()
    {
        foreach (var type in ScriptBlocks.Types)
        {
            Assert.True(Localization.TextCatalog.Entries.ContainsKey("Blocks.Type." + type.Id), type.Id);
            Assert.True(Localization.TextCatalog.Entries.ContainsKey("Blocks.Hint." + type.Id), type.Id);
            Assert.True(Localization.TextCatalog.Entries.ContainsKey("Blocks.Category." + type.Category), type.Category);
            foreach (var field in type.Fields)
            {
                Assert.True(Localization.TextCatalog.Entries.ContainsKey("Blocks.Field." + field.Name), field.Name);
                foreach (var option in field.Options ?? [])
                {
                    Assert.True(Localization.TextCatalog.Entries.ContainsKey("Blocks.Option." + option), option);
                }
            }
        }
    }
}
