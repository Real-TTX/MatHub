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
