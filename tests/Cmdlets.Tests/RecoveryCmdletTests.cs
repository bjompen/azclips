// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.Json;
using System.Net;
using Core.Abstractions;
using Core.Abstractions.Recovery;
using Core.DependencyInjection;
using Core.Runtime.Recovery;
using Microsoft.Azure.Azclips.Cmdlets.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Cmdlets.Tests;

public class RecoveryCmdletTests
{
    [Fact]
    public void GetRecoveryId_ListsAzclipsErrorsWithStableIds()
    {
        var state = InitialSessionState.CreateDefault2();
        state.Commands.Add(new SessionStateCmdletEntry("Connect-AzAccount", typeof(ConnectAccountCmdlet), null));
        state.Commands.Add(new SessionStateCmdletEntry("Get-AzRecoveryId", typeof(GetRecoveryIdCmdlet), null));
        state.Commands.Add(new SessionStateCmdletEntry("Send-AzRecovery", typeof(SendRecoveryCmdlet), null));
        using var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        var errors = ps.AddScript("""
            try { Connect-AzAccount -FirstBadParameter } catch { if ($_.FullyQualifiedErrorId -notlike 'NamedParameterNotFound,*') { throw } }
            try { Send-AzRecovery -Id invalid-id } catch {
                if ($_.Exception.Message -notmatch 'Invalid recovery ID') { throw }
            }
            try { Get-Item -SecondBadParameter } catch { if ($_.FullyQualifiedErrorId -notlike 'NamedParameterNotFound,*') { throw } }
            try { Connect-AzAccount -ThirdBadParameter } catch { if ($_.FullyQualifiedErrorId -notlike 'NamedParameterNotFound,*') { throw } }
            function Get-AzPretend { throw 'Not an AzCLIPS cmdlet' }
            try { Get-AzPretend } catch { if ($_.Exception.Message -ne 'Not an AzCLIPS cmdlet') { throw } }
            Get-AzRecoveryId
            Get-AzRecoveryId
            """).Invoke<RecoveryError>();

        Assert.Equal(4, errors.Count);
        Assert.Contains("ThirdBadParameter", errors[0].Error.Exception.Message);
        Assert.Contains("FirstBadParameter", errors[1].Error.Exception.Message);
        Assert.All(errors, error =>
        {
            Assert.True(FailureId.TryParse(error.Id, out var id));
            Assert.Equal((uint)Environment.ProcessId, id.TerminalSessionId);
        });
        Assert.NotEqual(errors[0].Id, errors[1].Id);
        Assert.Equal(errors[0].Id, errors[2].Id);
        Assert.Equal(errors[1].Id, errors[3].Id);
    }

    [Theory(Skip = "Deferred: detecting recovery resets requires changes outside the recovery cmdlets.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetRecoveryId_DoesNotListErrorsAfterReset(bool listBeforeReset)
    {
        var state = InitialSessionState.CreateDefault2();
        state.Commands.Add(new SessionStateCmdletEntry("Get-AzProfile", typeof(GetProfileCmdlet), null));
        state.Commands.Add(new SessionStateCmdletEntry("Get-AzRecoveryId", typeof(GetRecoveryIdCmdlet), null));
        using var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript("try { Get-AzProfile -BeforeReset } catch { if ($_.FullyQualifiedErrorId -notlike 'NamedParameterNotFound,*') { throw } }").Invoke();
        ps.Commands.Clear();

        if (listBeforeReset)
        {
            Assert.Single(ps.AddScript("Get-AzRecoveryId").Invoke<RecoveryError>());
            ps.Commands.Clear();
        }
        var configChange = new RecoveryConfigurationChangeHandler(Mock.Of<IFailureHistoryStore>());
        await configChange.OnChangedAsync(RecoveryConfigurationChangeHandler.TriggerKey, "off", CancellationToken.None);

        Assert.Empty(ps.AddScript("Get-AzRecoveryId").Invoke<RecoveryError>());
    }

    [Fact]
    public void DescribeError_IncludesConflictingParameterNamesAndDisplayedError()
    {
        var state = InitialSessionState.CreateDefault2();
        state.Commands.Add(new SessionStateCmdletEntry("Get-AzProfile", typeof(GetProfileCmdlet), null));
        using var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        var error = Assert.Single(ps.AddScript("""
            try { Get-AzProfile -Active -Name 'https://example.invalid/?sig=fake-secret' } catch { $_ }
            """).Invoke<ErrorRecord>());
        error.ErrorDetails = new ErrorDetails("Request URI: https://example.invalid/?sig=fake-secret");

        var diagnostic = SendRecoveryCmdlet.DescribeError(error);
        Assert.Contains("Conflicting or incomplete parameter set", diagnostic);
        Assert.Contains("-Active, -Name", diagnostic);
        Assert.Contains("Error: Request URI: https://example.invalid/?sig=fake-secret", diagnostic);
    }

    [Fact]
    public void GetRecoveryId_SnapshotsBeforeDownstreamClearsErrorList()
    {
        var state = InitialSessionState.CreateDefault2();
        state.Commands.Add(new SessionStateCmdletEntry("Get-AzProfile", typeof(GetProfileCmdlet), null));
        state.Commands.Add(new SessionStateCmdletEntry("Get-AzRecoveryId", typeof(GetRecoveryIdCmdlet), null));
        using var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        var result = ps.AddScript("""
            try { Get-AzProfile -FirstUnknown } catch { if ($_.FullyQualifiedErrorId -notlike 'NamedParameterNotFound,*') { throw } }
            try { Get-AzProfile -SecondUnknown } catch { if ($_.FullyQualifiedErrorId -notlike 'NamedParameterNotFound,*') { throw } }
            Get-AzRecoveryId | ForEach-Object { $Error.Clear(); $_ }
            """).Invoke<RecoveryError>();
        Assert.Empty(ps.Streams.Error);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DescribeError_SuggestsMetadataNamesAndIncludesUnknownParameter()
    {
        var state = InitialSessionState.CreateDefault2();
        state.Commands.Add(new SessionStateCmdletEntry("Get-AzProfile", typeof(GetProfileCmdlet), null));
        using var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        var error = Assert.Single(ps.AddScript("""
            try { Get-AzProfile -Nmae 'fake-secret' } catch { $_ }
            """).Invoke<ErrorRecord>());

        var diagnostic = SendRecoveryCmdlet.DescribeError(error);
        Assert.Contains("Possible parameter names: -Name", diagnostic);
        Assert.Contains("Nmae", diagnostic);
        Assert.DoesNotContain("fake-secret", diagnostic);
    }

    [Theory]
    [InlineData("Get-AzFixture -Status NotFound -Secret 'private-value'",
        "Get-AzFixture -Status <redacted> -Secret <redacted>")]
    [InlineData("Get-AzFixture 'private-value' -Status NotFound -Force",
        "Get-AzFixture <redacted> -Status <redacted> -Force")]
    [InlineData("Get-AzFixture -Status:NotFound -Secret:('private-value') -Force:$false",
        "Get-AzFixture -Status <redacted> -Secret <redacted> -Force <redacted>")]
    [InlineData("Get-AzFixture -Status NotFound -Secret ('private-value') | Out-Null",
        "Get-AzFixture -Status <redacted> -Secret <redacted>")]
    [InlineData("$privateSplat = @{ Status = 'NotFound'; Secret = 'private-value' }; Get-AzFixture @privateSplat",
        "Get-AzFixture <redacted-splat>")]
    public void DescribeError_ReconstructsCommandWithoutValues(string script, string expected)
    {
        var state = InitialSessionState.CreateDefault2();
        state.Commands.Add(new SessionStateCmdletEntry("Get-AzFixture", typeof(HttpErrorFixtureCmdlet), null));
        using var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(script).Invoke();

        var diagnostic = SendRecoveryCmdlet.DescribeError(Assert.Single(ps.Streams.Error));
        Assert.Contains($"Redacted command: {expected}\n", diagnostic);
        Assert.DoesNotContain("private-value", diagnostic);
        Assert.DoesNotContain("privateSplat", diagnostic);
        if (script.Contains("-Status", StringComparison.Ordinal))
            Assert.Contains("-Status", diagnostic[(diagnostic.IndexOf("Recognized parameters:", StringComparison.Ordinal))..]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public void DescribeError_UsesHttpStatusAndOriginalError(HttpStatusCode status)
    {
        var state = InitialSessionState.CreateDefault2();
        state.Commands.Add(new SessionStateCmdletEntry("Get-AzFixture", typeof(HttpErrorFixtureCmdlet), null));
        using var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddCommand("Get-AzFixture").AddParameter("Status", status).Invoke();
        var error = Assert.Single(ps.Streams.Error);
        var diagnostic = SendRecoveryCmdlet.DescribeError(error);
        Assert.Contains($"HTTP status: {(int)status}", diagnostic);
        Assert.Contains($"Error: {error}", diagnostic);
        Assert.Contains("example.invalid", diagnostic);
        Assert.Contains("fake-secret", diagnostic);
        Assert.Contains("ResourceGroupNotFound", diagnostic);
        Assert.Contains("Resource group 'notExists' could not be found.", diagnostic);
    }

    [Fact]
    public void ConfirmInteractive_RejectsHostThatCannotReadInput()
    {
        var ui = new Mock<PSHostUserInterface>();
        ui.Setup(host => host.ReadLine()).Throws(new PSInvalidOperationException("Noninteractive host"));
        var error = Assert.Throws<PSInvalidOperationException>(() => SendRecoveryCmdlet.ConfirmInteractive(ui.Object, "metadata"));
        Assert.Contains("-Style oneshot", error.Message);
    }

    [Fact]
    public void InteractiveAnswer_WritesToHostAndPipelineBeforeReadingInput()
    {
        var events = new List<string>();
        var ui = new Mock<PSHostUserInterface>();
        ui.Setup(host => host.WriteLine("Advice")).Callback(() => events.Add("host"));
        ui.Setup(host => host.ReadLine()).Returns(() =>
        {
            events.Add("read");
            return "next";
        });

        Assert.Equal("next", SendRecoveryCmdlet.WriteAnswerAndReadFollowUp("Advice", ui.Object, answer => events.Add(answer)));
        Assert.Equal(["host", "Advice", "read"], events);
    }

    [Theory]
    [InlineData("interactive", "OnEsHoT", "without asking follow-up questions", "low")]
    [InlineData("oneshot", "interactive", "Ask one focused follow-up question", "high")]
    [InlineData("interactive", null, "Ask one focused follow-up question", "medium")]
    [InlineData("oneshot", null, "without asking follow-up questions", "medium")]
    public async Task SendRecovery_PostsDiagnosticWithSelectedPowerShellPrompt(
        string configuredStyle, string? styleOverride, string expectedInstruction, string effort)
    {
        var state = InitialSessionState.CreateDefault2();
        state.Commands.Add(new SessionStateCmdletEntry("Get-AzFixture", typeof(HttpErrorFixtureCmdlet), null));
        using var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript("Get-AzFixture -Status NotFound -Secret 'private-value'").Invoke();
        var error = Assert.Single(ps.Streams.Error);
        var diagnostic = SendRecoveryCmdlet.DescribeError(error);
        Assert.Contains("Redacted command: Get-AzFixture -Status <redacted> -Secret <redacted>", diagnostic);

        using var http = new HttpClient(new ModelHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://example.invalid/openai/responses?api-version=2025-04-01-preview", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fake-token", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            Assert.Equal(effort, body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
            var input = body.RootElement.GetProperty("input")[0].GetProperty("content").GetString();
            Assert.Equal(diagnostic, input);
            Assert.Contains(error.ToString(), input);
            Assert.DoesNotContain("private-value", input);
            var instructions = body.RootElement.GetProperty("instructions").GetString();
            Assert.Contains("AzCLIPS Azure PowerShell module", instructions);
            Assert.Contains(expectedInstruction, instructions);
            Assert.Contains("placeholders, not actual values", instructions);
            Assert.Contains("error text is separate and may contain actual values", instructions);
            Assert.DoesNotContain("submit_response", instructions);

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"output":[{"type":"reasoning"},{"type":"message","content":[{"type":"output_text","text":"Suggested fix"}]}]}
                    """, Encoding.UTF8, "application/json"),
            };
        }));

        var options = new AgentOptions { Recovery = new RecoveryOptions { Style = configuredStyle }, ReasoningEffort = effort };
        var style = RecoverySessionSettings.Resolve(options, RecoveryTrigger.Manual, styleOverride);
        var answer = await SendRecoveryCmdlet.SendAsync(http, "https://example.invalid/openai/", options,
            "fake-token", [new("user", diagnostic)],
            style, CancellationToken.None);

        Assert.Equal("Suggested fix", answer);
    }

    [Theory]
    [InlineData("""{"error":{"code":"InvalidRequest","message":"Model is unavailable"}}""", "InvalidRequest", "Model is unavailable")]
    [InlineData("""{"error":{"code":"InvalidRequest","message":"client_secret=fake-secret"}}""", "InvalidRequest", "client_secret=fake-secret")]
    [InlineData("""{"error":{"code":"bad-secret?value=x","message":null}}""", null, null)]
    [InlineData("{broken", null, null)]
    [InlineData("""{"error":[]}""", null, null)]
    public async Task SendRecovery_SurfacesServiceErrorOrStatusFallback(string body, string? code, string? message)
    {
        using var http = new HttpClient(new ModelHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        })));
        var options = new AgentOptions();
        var style = RecoverySessionSettings.Resolve(options, RecoveryTrigger.Manual, "oneshot");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => SendRecoveryCmdlet.SendAsync(
            http, "https://example.invalid", options, "fake-token", [new("user", "diagnostic")], style, CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("HTTP 400", error.Message);
        if (code is not null) Assert.Contains(code, error.Message);
        if (message is not null) Assert.Contains(message, error.Message);
        Assert.DoesNotContain("bad-secret", error.Message);
    }

    private sealed class ModelHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    [Cmdlet(VerbsCommon.Get, "AzFixture")]
    public sealed class HttpErrorFixtureCmdlet : PSCmdlet
    {
        [Parameter] public HttpStatusCode Status { get; set; }
        [Parameter(Position = 0)] public string? Secret { get; set; }
        [Parameter] public SwitchParameter Force { get; set; }

        protected override void ProcessRecord()
        {
            var exception = new HttpRequestException(
                """404 Not Found from https://example.invalid/?sig=fake-secret: {"error":{"code":"ResourceGroupNotFound","message":"Resource group 'notExists' could not be found."}}""",
                null, Status);
            WriteError(new ErrorRecord(exception, "HttpError", ErrorCategory.NotSpecified, null));
        }
    }

    [Collection("Azclips Service Provider Tests")]
    public class RecoveryVerboseCmdletTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SendRecovery_ReportsInitialDiagnosticOnlyWhenVerbose(bool verbose)
        {
            AzclipsServiceProvider.Dispose();
            try
            {
                var configuration = new Mock<IConfigurationService>();
                configuration.Setup(service => service.GetConfiguration<AgentOptions>(null, null))
                    .Returns(new AgentOptions { Recovery = new RecoveryOptions { Trigger = "manual" } });
                var profiles = new Mock<IProfileService>();
                profiles.Setup(service => service.GetActiveProfileAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync((Profile?)null);
                AzclipsServiceProvider.OverrideRegistration(services =>
                {
                    services.AddSingleton(configuration.Object);
                    services.AddSingleton(profiles.Object);
                });
                AzclipsServiceProvider.Initialize(new ClientMetadata(ClientType.PowerShell, "test"));

                var state = InitialSessionState.CreateDefault2();
                state.Commands.Add(new SessionStateCmdletEntry("Get-AzProfile", typeof(GetProfileCmdlet), null));
                state.Commands.Add(new SessionStateCmdletEntry("Get-AzRecoveryId", typeof(GetRecoveryIdCmdlet), null));
                state.Commands.Add(new SessionStateCmdletEntry("Send-AzRecovery", typeof(SendRecoveryCmdlet), null));
                using var runspace = RunspaceFactory.CreateRunspace(state);
                runspace.Open();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;
                var failures = ps.AddScript("""
                    try { Get-AzProfile -Nmae 'fake-secret' } catch { if ($_.FullyQualifiedErrorId -notlike 'NamedParameterNotFound,*') { throw } }
                    Get-AzRecoveryId
                    """).Invoke<RecoveryError>();
                var diagnostic = SendRecoveryCmdlet.DescribeError(Assert.Single(failures).Error);
                ps.Commands.Clear();
                ps.AddCommand("Send-AzRecovery").AddParameter("Style", "oneshot");
                if (verbose)
                    ps.AddParameter("Verbose");

                var error = Assert.ThrowsAny<RuntimeException>(() => ps.Invoke());
                Assert.Contains("Sign in with Connect-AzAccount", error.Message);
                Assert.Contains(ps.Streams.Warning, record => record.Message.Contains("without redaction"));
                if (verbose)
                {
                    Assert.Contains(ps.Streams.Verbose, record => record.Message == $"Azure AI diagnostic:\n{diagnostic}");
                    Assert.DoesNotContain("fake-secret", diagnostic);
                }
                else
                {
                    Assert.Empty(ps.Streams.Verbose);
                }
            }
            finally
            {
                AzclipsServiceProvider.Dispose();
            }
        }
    }
}
