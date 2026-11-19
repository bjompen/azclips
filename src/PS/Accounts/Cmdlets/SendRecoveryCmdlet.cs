// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Language;
using Core.Abstractions;
using Core.Abstractions.Recovery;
using Core.Agent.Providers;
using Core.DependencyInjection;
using Core.Telemetry;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParameterMetadata = System.Management.Automation.ParameterMetadata;

namespace Microsoft.Azure.Azclips.Cmdlets.Accounts;

/// <summary>
/// Sends a selected AzCLIPS PowerShell error, or the latest one, to Azure AI for recovery advice.
/// </summary>
[Cmdlet(VerbsCommunications.Send, "AzRecovery", SupportsShouldProcess = true)]
[OutputType(typeof(string))]
public class SendRecoveryCmdlet : PSCmdlet
{
    private static readonly HttpClient s_http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(10),
    };
    private readonly CancellationTokenSource _cts = new();

    [Parameter]
    public string? Id { get; set; }

    [Parameter]
    [ValidateSet("oneshot", "interactive", IgnoreCase = true)]
    public string? Style { get; set; }

    protected override void ProcessRecord()
    {
        if (Id is not null && !FailureId.TryParse(Id, out _))
            throw new PSArgumentException("Invalid recovery ID. Use the format <process-id>-<8 lowercase hex digits>, such as 48212-cfd9ddb4.", nameof(Id));

        var options = AzclipsServiceProvider.GetRequiredService<IConfigurationService>().GetConfiguration<AgentOptions>();
        if (!options.Recovery.IsEnabled)
            throw new PSInvalidOperationException(
                "AI recovery is off. Set agent.recovery.trigger to manual or auto before troubleshooting. " +
                "Install and import the AzCLIPS Az.Config module, then run: Set-AzConfig -Key agent.recovery.trigger -Value manual");
        if (!options.IsBuiltinProvider)
            throw new PSInvalidOperationException("PowerShell recovery requires agent.provider to be builtin.");

        var failure = PowerShellRecoveryHistory.Read(SessionState).FirstOrDefault(error => Id is null || Id == error.Id)
            ?? throw new PSInvalidOperationException(Id is null
                ? "No Azure PowerShell errors are available in the current session."
                : "That recovery ID is not in this session. Use Get-AzRecoveryId to list available errors.");

        var style = RecoverySessionSettings.Resolve(options, RecoveryTrigger.Manual, Style);
        if (!ShouldProcess(failure.Id, "Send Azure PowerShell error to Azure AI"))
            return;

        var diagnostic = DescribeError(failure.Error);
        var ui = style.IsInteractive ? Host.UI : null;
        if (style.IsInteractive && !ConfirmInteractive(ui, diagnostic))
            return;

        WriteWarning("The error message is sent to Azure AI without redaction and may contain sensitive information.");
        WriteVerbose($"Azure AI diagnostic:\n{diagnostic}");

        var endpointWarnings = new List<string>();
        var (endpoint, accessToken) = ResolveModelAsync(options, endpointWarnings.Add, _cts.Token).GetAwaiter().GetResult();
        foreach (var warning in endpointWarnings)
            WriteWarning(warning);

        List<RecoveryMessage> messages =
        [
            new("user", diagnostic),
        ];
        while (true)
        {
            var answer = SendAsync(s_http, endpoint, options, accessToken, messages, style, _cts.Token).GetAwaiter().GetResult();
            var followUp = WriteAnswerAndReadFollowUp(answer, ui, WriteObject);
            if (followUp is null)
                return;
            messages.Add(new("assistant", answer));
            messages.Add(new("user", followUp));
        }
    }

    internal static string DescribeError(ErrorRecord error)
    {
        var invocation = error.InvocationInfo;
        var cmdlet = (CmdletInfo)invocation.MyCommand;
        var parameterNames = invocation.BoundParameters.Keys.ToList();
        var command = FindInvokedCommand(invocation, cmdlet);
        if (command is not null)
            parameterNames.AddRange(command.CommandElements.OfType<CommandParameterAst>()
                .Select(parameter => parameter.ParameterName));
        var bindingError = error.Exception as ParameterBindingException;
        if (!string.IsNullOrEmpty(bindingError?.ParameterName))
            parameterNames.Add(bindingError.ParameterName);
        var kind = bindingError?.ErrorId switch
        {
            "AmbiguousParameterSet" => "Conflicting or incomplete parameter set",
            "NamedParameterNotFound" => "Unknown parameter",
            "MissingMandatoryParameter" => "Missing mandatory parameter",
            _ when bindingError is not null => "Parameter binding failed",
            _ => null,
        };
        var metadata = cmdlet.Parameters;
        var parameters = parameterNames.Select(name => FindParameterName(metadata, name))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Select(name => $"-{name}")
            .Order(StringComparer.Ordinal);
        var candidates = bindingError?.ErrorId == "NamedParameterNotFound"
            ? parameterNames.Where(name => FindParameterName(metadata, name) is null)
                .SelectMany(name => FindCandidates(metadata, name)).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray()
            : [];
        var status = error.Exception is HttpRequestException { StatusCode: { } statusCode }
            ? $"\nHTTP status: {(int)statusCode}" : "";
        return $"Command: {cmdlet.Name}\nRedacted command: {DescribeCommand(command, cmdlet)}" +
            $"\nError category: {error.CategoryInfo.Category}" +
            $"\nError: {error}" +
            (kind is null ? "" : $"\nBinding error: {kind}") +
            $"\nRecognized parameters: {string.Join(", ", parameters)}" +
            (candidates.Length == 0 ? "" : $"\nPossible parameter names: {string.Join(", ", candidates.Select(name => $"-{name}"))}") +
            status;
    }

    private static CommandAst? FindInvokedCommand(InvocationInfo invocation, CmdletInfo cmdlet)
    {
        var text = string.IsNullOrWhiteSpace(invocation.Statement) ? invocation.Line : invocation.Statement;
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var ast = Parser.ParseInput(text, out _, out var errors);
        if (errors.Length != 0)
            return null;

        var commands = ast.FindAll(node => node is CommandAst, true).OfType<CommandAst>()
            .Where(command => string.Equals(command.GetCommandName(), invocation.InvocationName, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(command.GetCommandName(), cmdlet.Name, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return commands.Length == 1 ? commands[0] : null;
    }

    private static string DescribeCommand(CommandAst? command, CmdletInfo cmdlet)
    {
        if (command is null || command.CommandElements.Any(element =>
                element is StringConstantExpressionAst { Value: "--%" }))
            return "unavailable";

        var parts = new List<string> { cmdlet.Name };
        var elements = command.CommandElements;
        for (var i = 1; i < elements.Count; i++)
        {
            if (elements[i] is CommandParameterAst parameter)
            {
                var name = parameter.ParameterName;
                if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                    return "unavailable";
                parts.Add($"-{name}");
                var canonicalName = FindParameterName(cmdlet.Parameters, name);
                var isSwitch = canonicalName is not null &&
                    cmdlet.Parameters[canonicalName].ParameterType == typeof(SwitchParameter);
                if (parameter.Argument is not null)
                    parts.Add("<redacted>");
                else if (!isSwitch && i + 1 < elements.Count && elements[i + 1] is not CommandParameterAst &&
                         elements[i + 1] is not VariableExpressionAst { Splatted: true })
                {
                    parts.Add("<redacted>");
                    i++;
                }
            }
            else
            {
                parts.Add(elements[i] is VariableExpressionAst { Splatted: true } ? "<redacted-splat>" : "<redacted>");
            }
        }
        return string.Join(" ", parts);
    }

    private static IEnumerable<string> FindCandidates(Dictionary<string, ParameterMetadata> metadata, string name)
    {
        if (name.Length is < 2 or > 64)
            return [];
        return metadata.Values
            .Select(parameter => (parameter.Name, Distance: EditDistance(name, parameter.Name)))
            .Where(item => item.Distance <= 2)
            .OrderBy(item => item.Distance).ThenBy(item => item.Name, StringComparer.Ordinal)
            .Take(5).Select(item => item.Name);
    }

    private static int EditDistance(string left, string right)
    {
        var distances = new int[left.Length + 1, right.Length + 1];
        for (var i = 0; i <= left.Length; i++) distances[i, 0] = i;
        for (var j = 0; j <= right.Length; j++) distances[0, j] = j;
        for (var i = 1; i <= left.Length; i++)
        for (var j = 1; j <= right.Length; j++)
        {
            var cost = char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 1]) ? 0 : 1;
            distances[i, j] = Math.Min(Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1), distances[i - 1, j - 1] + cost);
            if (i > 1 && j > 1 && char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 2]) &&
                char.ToUpperInvariant(left[i - 2]) == char.ToUpperInvariant(right[j - 1]))
                distances[i, j] = Math.Min(distances[i, j], distances[i - 2, j - 2] + 1);
        }
        return distances[left.Length, right.Length];
    }

    private static string? FindParameterName(Dictionary<string, ParameterMetadata> metadata, string name)
    {
        var exact = metadata.Values.FirstOrDefault(parameter =>
            parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            parameter.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (exact is not null)
            return exact.Name;

        var matches = metadata.Values.Where(parameter => parameter.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) ||
            parameter.Aliases.Any(alias => alias.StartsWith(name, StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Name : null;
    }

    internal static bool ConfirmInteractive(PSHostUserInterface? ui, string diagnostic)
    {
        const string unsupported = "Interactive recovery requires a host that can read input. Use -Style oneshot instead.";
        if (ui is null)
            throw new PSInvalidOperationException(unsupported);

        string? answer;
        try
        {
            ui.WriteLine("Azure AI will receive this diagnostic:");
            ui.WriteLine(diagnostic);
            ui.WriteLine("The diagnostic and follow-ups are sent without redaction. Do not include secrets or sensitive data.");
            ui.Write("Start interactive recovery? [y/N]: ");
            answer = ui.ReadLine();
        }
        catch (Exception ex) when (ex is PSInvalidOperationException or HostException or NotSupportedException)
        {
            throw new PSInvalidOperationException(unsupported, ex);
        }

        return answer is null
            ? throw new PSInvalidOperationException(unsupported)
            : string.Equals(answer.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? WriteAnswerAndReadFollowUp(string answer, PSHostUserInterface? ui, Action<string> writeOutput)
    {
        ui?.WriteLine(answer);
        writeOutput(answer);
        if (ui is null)
            return null;

        ui.Write("Follow-up (Enter to finish): ");
        var followUp = ui.ReadLine();
        return string.IsNullOrWhiteSpace(followUp) ? null : followUp;
    }

    private static async Task<(string Endpoint, string AccessToken)> ResolveModelAsync(
        AgentOptions options, Action<string> reportWarning, CancellationToken token)
    {
        var profile = await AzclipsServiceProvider.GetRequiredService<IProfileService>()
            .GetActiveProfileAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Sign in with Connect-AzAccount before using AI recovery.");
        var auth = await AzclipsServiceProvider.GetRequiredService<IAuthService>()
            .AcquireTokenSilentlyAsync([BuiltinProviderOptions.ArmScope], profile.TenantId,
                cancellationToken: token, profile: profile).ConfigureAwait(false);
        if (string.IsNullOrEmpty(auth.AccessToken))
            throw new InvalidOperationException("Sign in with Connect-AzAccount before using AI recovery.");

        var boundary = await AzclipsServiceProvider.GetRequiredService<DataBoundaryResolver>()
            .ResolveAndCacheAsync(profile.TenantId, _ => Task.FromResult<string?>(auth.AccessToken),
                TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        return (BuiltinEndpointResolver.Choose(boundary, options.Builtin, reportWarning).Url, auth.AccessToken);
    }

    internal sealed record RecoveryMessage(string Role, string Content);

    internal static async Task<string> SendAsync(HttpClient http, string endpoint, AgentOptions options,
        string accessToken, IReadOnlyList<RecoveryMessage> messages, RecoverySessionSettings style, CancellationToken token)
    {
        var promptName = $"RecoveryPrompts.{style.SessionStyle}.md";
        using var prompt = new StreamReader(typeof(SendRecoveryCmdlet).Assembly.GetManifestResourceStream(promptName)
            ?? throw new InvalidOperationException($"The embedded PowerShell recovery prompt '{promptName}' was not found."));
        var url = $"{endpoint.TrimEnd('/')}/responses?api-version={Uri.EscapeDataString(options.Builtin.ApiVersion)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            model = options.Builtin.ModelId,
            reasoning = new { effort = options.ReasoningEffort.ToLowerInvariant() },
            instructions = await prompt.ReadToEndAsync(token).ConfigureAwait(false),
            input = messages,
            store = false,
        });
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateServiceExceptionAsync(response, token).ConfigureAwait(false);
        using var content = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Azure AI returned an empty recovery response.");

        var text = from item in content.RootElement.GetProperty("output").EnumerateArray()
                   where item.GetProperty("type").GetString() == "message"
                   from part in item.GetProperty("content").EnumerateArray()
                   where part.GetProperty("type").GetString() == "output_text"
                   select part.GetProperty("text").GetString();
        var answer = string.Join(Environment.NewLine, text);
        return !string.IsNullOrWhiteSpace(answer) ? answer
            : throw new InvalidOperationException("Azure AI returned no recovery advice.");
    }

    private static async Task<HttpRequestException> CreateServiceExceptionAsync(HttpResponseMessage response, CancellationToken token)
    {
        var prefix = $"Azure AI request failed with HTTP {(int)response.StatusCode}";
        try
        {
            using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: token).ConfigureAwait(false);
            if (json?.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                return new HttpRequestException(prefix, null, response.StatusCode);

            var code = error.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String
                ? codeValue.GetString() : null;
            var message = error.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String
                ? messageValue.GetString() : null;
            if (IsSafeServiceCode(code))
                prefix += $" ({code})";
            if (!string.IsNullOrWhiteSpace(message))
            {
                if (message.Length > 2048)
                    message = message[..2048];
                message = new string(message.Where(c => !char.IsControl(c)).Take(512).ToArray());
                if (!string.IsNullOrWhiteSpace(message))
                    prefix += $": {message}";
            }
        }
        catch (JsonException)
        {
            // A malformed service body must not hide the HTTP status.
        }
        return new HttpRequestException(prefix, null, response.StatusCode);
    }

    private static bool IsSafeServiceCode(string? code) =>
        code is { Length: > 0 and <= 64 } && char.IsAsciiLetter(code[0]) &&
        code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    protected override void EndProcessing() => _cts.Dispose();

    protected override void StopProcessing() => _cts.Cancel();
}
