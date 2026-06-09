using System.Net;
using System.Text;
using System.Text.Json;
using LEPTA.Services;
using LEPTA.Shared.Models;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Tests;

[TestFixture]
[NonParallelizable]
public sealed class VllmMermaidTroubleshootIntegrationTests
{
    private const string ProblematicClipboardText = """
        The Dependency Inversion Principle (DIP)

        The Dependency Inversion Principle (DIP) states that a high-level class must not depend upon a lower-level class. They must both depend upon abstractions. And secondly, an abstraction must not depend upon details, but the details must depend upon abstractions. This will ensure the class and, ultimately, the whole application is very robust and easy to maintain and expand, if required. Let us look at this with an example..
        """;

    private static string BaseUrl => Environment.GetEnvironmentVariable("VLLM_BASE_URL") ?? "http://localhost:8512";

    [Test]
    [Category("Unit")]
    public void MermaidSourceNormalizer_StripsCleanFence()
    {
        var raw = "```mermaid\nclassDiagram\nA-->B\n```";
        var normalized = MermaidSourceNormalizer.Normalize(raw);
        Assert.That(normalized, Does.StartWith("classDiagram"));
    }

    [Test]
    [Category("Unit")]
    public void MermaidSourceNormalizer_StripsFenceWithExtraText()
    {
        var raw = "Here is the diagram:\n```mermaid\nclassDiagram\nA-->B\n```\nSome extra text.";
        var normalized = MermaidSourceNormalizer.Normalize(raw);
        Assert.That(normalized, Does.StartWith("classDiagram"));
        Assert.That(normalized, Does.Not.Contain("Here is the diagram"));
        Assert.That(normalized, Does.Not.Contain("Some extra text"));
    }

    [Test]
    [Category("Unit")]
    public void MermaidSourceNormalizer_StripsFirstFence_WhenMultipleFences()
    {
        var raw = "```mermaid\ngraph TD\nA-->B\n```\n\n```mermaid\ngraph TD\nC-->D\n```";
        var normalized = MermaidSourceNormalizer.Normalize(raw);
        Assert.That(normalized, Does.StartWith("graph TD"));
        Assert.That(normalized, Does.Not.Contain("C-->D"));
    }

    [Test]
    [Category("Integration")]
    public async Task MultiplePanelRequests_WithProblematicClipboard_ReportsFailures()
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        var logger = new TestLeptaLogger();
        TestContext.Progress.WriteLine($"[Troubleshoot] Checking vLLM health at {BaseUrl}/health");
        await EnsureServerReachableAsync(http, BaseUrl);

        TestContext.Progress.WriteLine($"[Troubleshoot] Resolving first model via {BaseUrl}/v1/models");
        var model = await ResolveFirstModelIdAsync(http, BaseUrl);

        var client = new VllmChatCompletionClient(http, logger);
        var conversationService = new VllmConversationService(client, logger);
        var orchestrator = new LeptaRequestOrchestrator(conversationService, logger);

        var panels = new List<LeptaPanelRequest>
        {
            new("Summary", "Summarize the key concepts in 3 bullet points."),
            new("Architecture", "Generate a compact UML or architecture-style diagram.\n\nRules:\n- Focus on major components and interactions\n- Keep readable and minimal\n- Mermaid-compatible", LeptaPanelFormats.Mermaid),
            new("Risks", "List the top 3 risks or pitfalls mentioned."),
            new("Diagram", "Create a simple Mermaid flowchart showing the relationships described.", LeptaPanelFormats.Mermaid),
            new("Explanation", "Explain the main principle in one short paragraph.")
        };

        const int iterationCount = 3;
        var allResults = new List<IterationResult>();

        for (int i = 0; i < iterationCount; i++)
        {
            TestContext.Progress.WriteLine($"[Troubleshoot] Starting iteration {i + 1}/{iterationCount}");
            var iteration = await RunIterationAsync(orchestrator, model, panels, i, logger);
            allResults.Add(iteration);
        }

        AnalyzeAndReport(allResults, logger);
    }

    [Test]
    [Category("Integration")]
    public async Task MermaidOnlyStressTest_WithProblematicClipboard_ReportsFailures()
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        var logger = new TestLeptaLogger();
        TestContext.Progress.WriteLine($"[Troubleshoot] Checking vLLM health at {BaseUrl}/health");
        await EnsureServerReachableAsync(http, BaseUrl);

        TestContext.Progress.WriteLine($"[Troubleshoot] Resolving first model via {BaseUrl}/v1/models");
        var model = await ResolveFirstModelIdAsync(http, BaseUrl);

        var client = new VllmChatCompletionClient(http, logger);
        var conversationService = new VllmConversationService(client, logger);
        var orchestrator = new LeptaRequestOrchestrator(conversationService, logger);

        var mermaidPanels = new List<LeptaPanelRequest>
        {
            new("UML", "Generate a compact UML or architecture-style diagram.\n\nRules:\n- Focus on major components and interactions\n- Keep readable and minimal\n- Mermaid-compatible", LeptaPanelFormats.Mermaid),
            new("Flowchart", "Create a Mermaid flowchart of the described relationships.", LeptaPanelFormats.Mermaid),
            new("ClassDiagram", "Generate a Mermaid class diagram illustrating the principle.", LeptaPanelFormats.Mermaid),
            new("Sequence", "Generate a Mermaid sequence diagram showing the interaction flow.", LeptaPanelFormats.Mermaid)
        };

        const int iterationCount = 3;
        var allResults = new List<IterationResult>();

        for (int i = 0; i < iterationCount; i++)
        {
            TestContext.Progress.WriteLine($"[Troubleshoot] Mermaid-only iteration {i + 1}/{iterationCount}");
            var iteration = await RunIterationAsync(orchestrator, model, mermaidPanels, i, logger);
            allResults.Add(iteration);
        }

        AnalyzeAndReport(allResults, logger);
    }

    private async Task<IterationResult> RunIterationAsync(
        LeptaRequestOrchestrator orchestrator,
        string model,
        IReadOnlyList<LeptaPanelRequest> panels,
        int iterationIndex,
        TestLeptaLogger logger)
    {
        var panelResults = new List<PanelResult>();
        var errors = new List<string>();

        try
        {
            var responses = await orchestrator.GenerateForPanelsAsync(
                BaseUrl,
                model,
                "You are a precise technical assistant.",
                ProblematicClipboardText,
                "Be concise and accurate.",
                panels,
                enableThinking: false,
                temperature: 0.2,
                cancellationToken: TestContext.CurrentContext.CancellationToken);

            for (int i = 0; i < responses.Count; i++)
            {
                var response = responses[i];
                var panel = panels[i];
                panelResults.Add(new PanelResult(
                    panel.Name,
                    panel.Format,
                    response.Text,
                    response.Error,
                    response.GenerationDuration,
                    response.EstimatedVisibleTokenCount));

                if (!string.IsNullOrWhiteSpace(response.Error))
                {
                    errors.Add($"[{panel.Name}] {response.Error}");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Iteration-level exception: {ex.GetType().Name}: {ex.Message}");
        }

        return new IterationResult(iterationIndex, panelResults, errors, logger.Entries.ToList());
    }

    private void AnalyzeAndReport(List<IterationResult> allResults, TestLeptaLogger logger)
    {
        var builder = new StringBuilder();
        builder.AppendLine("=== vLLM Troubleshoot Analysis ===");
        builder.AppendLine();

        var totalRequests = allResults.Sum(r => r.Panels.Count);
        var failedPanels = allResults.SelectMany(r => r.Panels).Where(p => !string.IsNullOrWhiteSpace(p.Error)).ToList();
        var successPanels = allResults.SelectMany(r => r.Panels).Where(p => string.IsNullOrWhiteSpace(p.Error)).ToList();
        var emptyResponses = successPanels.Where(p => string.IsNullOrWhiteSpace(p.Text)).ToList();
        var mermaidPanels = allResults.SelectMany(r => r.Panels).Where(p => p.Format == LeptaPanelFormats.Mermaid).ToList();
        var mermaidFailures = mermaidPanels.Where(p => !string.IsNullOrWhiteSpace(p.Error)).ToList();
        var mermaidEmpty = mermaidPanels.Where(p => string.IsNullOrWhiteSpace(p.Text) && string.IsNullOrWhiteSpace(p.Error)).ToList();

        // Analyze Mermaid syntax quality
        var mermaidQualityIssues = mermaidPanels
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .Select(p => new { p.Name, p.Text, Issues = DetectMermaidQualityIssues(p.Text) })
            .Where(x => x.Issues.Any())
            .ToList();

        builder.AppendLine($"Total iterations: {allResults.Count}");
        builder.AppendLine($"Total panel requests: {totalRequests}");
        builder.AppendLine($"Successful panels: {successPanels.Count} ({(successPanels.Count * 100.0 / totalRequests):F1}%)");
        builder.AppendLine($"Failed panels: {failedPanels.Count} ({(failedPanels.Count * 100.0 / totalRequests):F1}%)");
        builder.AppendLine($"Empty but no error: {emptyResponses.Count}");
        builder.AppendLine();
        builder.AppendLine($"Mermaid requests: {mermaidPanels.Count}");
        builder.AppendLine($"Mermaid failures: {mermaidFailures.Count} ({(mermaidFailures.Count * 100.0 / mermaidPanels.Count):F1}%)");
        builder.AppendLine($"Mermaid empty responses: {mermaidEmpty.Count}");
        builder.AppendLine($"Mermaid quality issues: {mermaidQualityIssues.Count}");
        builder.AppendLine();

        var failureCategories = failedPanels
            .GroupBy(p => CategorizeError(p.Error))
            .Select(g => new { Category = g.Key, Count = g.Count(), Examples = g.Take(3).Select(p => $"[{p.Name}] {p.Error}").ToList() })
            .ToList();

        builder.AppendLine("=== Failure Categories ===");
        foreach (var category in failureCategories)
        {
            builder.AppendLine($"Category: {category.Category} ({category.Count} occurrences)");
            foreach (var example in category.Examples)
            {
                builder.AppendLine($"  Example: {example}");
            }
            builder.AppendLine();
        }

        var iterationErrors = allResults.Where(r => r.Errors.Any()).ToList();
        builder.AppendLine("=== Iteration-Level Errors ===");
        builder.AppendLine($"Iterations with errors: {iterationErrors.Count}/{allResults.Count}");
        foreach (var iteration in iterationErrors)
        {
            builder.AppendLine($"Iteration {iteration.Index}: {string.Join("; ", iteration.Errors)}");
        }
        builder.AppendLine();

        builder.AppendLine("=== Mermaid Quality Issues ===");
        var qualityCategories = mermaidQualityIssues
            .SelectMany(x => x.Issues)
            .GroupBy(i => i)
            .Select(g => new { Issue = g.Key, Count = g.Count() })
            .ToList();
        foreach (var qc in qualityCategories)
        {
            builder.AppendLine($"{qc.Issue}: {qc.Count} occurrences");
        }
        builder.AppendLine();
        foreach (var issue in mermaidQualityIssues.Take(5))
        {
            builder.AppendLine($"--- {issue.Name} issues: {string.Join(", ", issue.Issues)} ---");
            builder.AppendLine(issue.Text!.Length > 400 ? issue.Text[..400] + "..." : issue.Text);
            builder.AppendLine();
        }

        builder.AppendLine("=== Mermaid Output Samples ===");
        var mermaidSamples = mermaidPanels
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .Take(5)
            .ToList();
        foreach (var sample in mermaidSamples)
        {
            builder.AppendLine($"--- {sample.Name} ---");
            builder.AppendLine(sample.Text!.Length > 500 ? sample.Text![..500] + "..." : sample.Text!);
            builder.AppendLine();
        }

        var report = builder.ToString();
        TestContext.Progress.WriteLine(report);
        TestContext.Out.WriteLine(report);

        Assert.Warn($"Troubleshoot complete. Failures: {failedPanels.Count}/{totalRequests}. Mermaid quality issues: {mermaidQualityIssues.Count}. See test output for full analysis.");
    }

    private static List<string> DetectMermaidQualityIssues(string? text)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return issues;

        // Strip fence first to analyze actual Mermaid code
        var normalized = StripMermaidFence(text).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(normalized))
            return issues;

        // Check for markdown fences remaining inside the diagram after stripping
        if (normalized.Contains("```mermaid", StringComparison.Ordinal))
            issues.Add("ContainsInnerMermaidFence");
        if (normalized.Contains("```", StringComparison.Ordinal))
            issues.Add("ContainsBacktickFence");

        // Check for empty or trivial diagrams
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count < 2)
            issues.Add("TooFewLines");

        // Check for valid Mermaid start keywords
        var validStarts = new[] { "graph", "flowchart", "sequencediagram", "classdiagram", "statediagram", "erdiagram", "gantt", "pie", "journey", "gitgraph", "mindmap", "timeline", "requirementdiagram", "flowchart" };
        var firstLine = lines.FirstOrDefault()?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!validStarts.Any(v => firstLine.StartsWith(v, StringComparison.Ordinal)))
            issues.Add($"InvalidMermaidStart(firstLine={firstLine.Replace("\n", "\\n").Replace("\r", "\\r")})");

        // Check for unclosed brackets or parentheses that might break parsing
        var openSquare = normalized.Count(c => c == '[');
        var closeSquare = normalized.Count(c => c == ']');
        if (openSquare != closeSquare)
            issues.Add("UnbalancedBrackets");

        var openParen = normalized.Count(c => c == '(');
        var closeParen = normalized.Count(c => c == ')');
        if (openParen != closeParen)
            issues.Add("UnbalancedParentheses");

        // Check for duplicate node definitions with conflicting labels
        var nodeDefinitions = new Dictionary<string, string>();
        var nodeDefPattern = System.Text.RegularExpressions.Regex.Matches(normalized, @"(\b[A-Za-z0-9_]+\b)\s*\[([^\]]+)\]");
        foreach (System.Text.RegularExpressions.Match match in nodeDefPattern)
        {
            var id = match.Groups[1].Value;
            var label = match.Groups[2].Value.Trim();
            if (nodeDefinitions.TryGetValue(id, out var existing) && existing != label)
            {
                issues.Add("ConflictingNodeLabels");
                break;
            }
            nodeDefinitions[id] = label;
        }

        // Check for Mermaid keywords used as node IDs
        var mermaidKeywords = new[] { "class", "style", "linkStyle", "click", "subgraph", "end", "direction", "participant", "loop", "alt", "else", "opt", "par", "and", "rect", "note", "over" };
        foreach (var match in nodeDefPattern.Cast<System.Text.RegularExpressions.Match>())
        {
            var id = match.Groups[1].Value.ToLowerInvariant();
            if (mermaidKeywords.Contains(id))
            {
                issues.Add("ReservedKeywordAsNodeId");
                break;
            }
        }

        // Check for invalid note syntax in classDiagram: note NodeId "text" without "for"
        if (firstLine.Contains("classDiagram", StringComparison.Ordinal))
        {
            var invalidNotePattern = System.Text.RegularExpressions.Regex.Matches(normalized, @"^\s*note\s+(?!for\s+)([A-Za-z0-9_]+)\s+""([^""]*)""", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (invalidNotePattern.Count > 0)
                issues.Add("InvalidNoteSyntaxMissingFor");

            // Check for floating note syntax that might be wrong: note "text" without for is actually valid in some contexts
            // But note NodeId "text" is definitely invalid
        }

        // Check for class definitions with malformed method signatures
        if (firstLine.Contains("classDiagram", StringComparison.Ordinal))
        {
            var malformedClassPattern = System.Text.RegularExpressions.Regex.Matches(normalized, @"class\s+[A-Za-z0-9_]+\s*\{\s*[^}]*\+[^()]*\}");
            foreach (System.Text.RegularExpressions.Match match in malformedClassPattern)
            {
                var content = match.Value;
                // If it contains +FieldName without () and without type, it might be invalid
                if (System.Text.RegularExpressions.Regex.IsMatch(content, @"\+\s*[A-Za-z0-9_]+\s*\}"))
                {
                    issues.Add("MalformedClassMember");
                    break;
                }
            }
        }

        return issues.Distinct().ToList();
    }

    private static string StripMermaidFence(string? mermaidBlock)
        => MermaidSourceNormalizer.Normalize(mermaidBlock);

    private static string CategorizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "Unknown";

        if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "Timeout";
        if (error.Contains("status 400", StringComparison.OrdinalIgnoreCase) || error.Contains("BadRequest", StringComparison.OrdinalIgnoreCase))
            return "HTTP 400 - Bad Request";
        if (error.Contains("status 404", StringComparison.OrdinalIgnoreCase))
            return "HTTP 404 - Not Found";
        if (error.Contains("status 422", StringComparison.OrdinalIgnoreCase))
            return "HTTP 422 - Unprocessable Entity";
        if (error.Contains("status 500", StringComparison.OrdinalIgnoreCase) || error.Contains("status 502", StringComparison.OrdinalIgnoreCase) || error.Contains("status 503", StringComparison.OrdinalIgnoreCase))
            return "HTTP 5xx - Server Error";
        if (error.Contains("HttpRequestException", StringComparison.OrdinalIgnoreCase) || error.Contains("Could not reach", StringComparison.OrdinalIgnoreCase))
            return "Connection/Network";
        if (error.Contains("cancel", StringComparison.OrdinalIgnoreCase) || error.Contains("OperationCanceled", StringComparison.OrdinalIgnoreCase))
            return "Cancellation";
        if (error.Contains("InvalidOperation", StringComparison.OrdinalIgnoreCase))
            return "InvalidOperation";

        return "Other";
    }

    private static async Task EnsureServerReachableAsync(HttpClient http, string baseUrl)
    {
        HttpResponseMessage healthResponse;
        try
        {
            healthResponse = await http.GetAsync($"{baseUrl.TrimEnd('/')}/health");
        }
        catch (HttpRequestException exception)
        {
            Assert.Ignore(
                $"vLLM server is not reachable at {baseUrl}. Start docker image from LEPTA.vLLM/dev/dockerfile.vLLM-Dev first. {exception.Message}");
            return;
        }

        if (!healthResponse.IsSuccessStatusCode)
        {
            Assert.Ignore(
                $"vLLM server is not reachable at {baseUrl}. Start docker image from LEPTA.vLLM/dev/dockerfile.vLLM-Dev first. HTTP {(int)healthResponse.StatusCode} ({healthResponse.ReasonPhrase}).");
        }
    }

    [Test]
    [Category("Integration")]
    public async Task MermaidRepairFlow_WithProblematicGeneratedMermaid_ReportsRepairSuccessRate()
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        var logger = new TestLeptaLogger();
        TestContext.Progress.WriteLine($"[Troubleshoot] Checking vLLM health at {BaseUrl}/health");
        await EnsureServerReachableAsync(http, BaseUrl);

        TestContext.Progress.WriteLine($"[Troubleshoot] Resolving first model via {BaseUrl}/v1/models");
        var model = await ResolveFirstModelIdAsync(http, BaseUrl);

        var client = new VllmChatCompletionClient(http, logger);
        var conversationService = new VllmConversationService(client, logger);
        var orchestrator = new LeptaRequestOrchestrator(conversationService, logger);

        // First, generate some Mermaid diagrams that might be broken
        var generatePanel = new LeptaPanelRequest("UML", "Generate a compact UML or architecture-style diagram.\n\nRules:\n- Focus on major components and interactions\n- Keep readable and minimal\n- Mermaid-compatible", LeptaPanelFormats.Mermaid);
        var prompt = LeptaRequestOrchestrator.BuildPrompt(
            "You are a precise technical assistant.",
            ProblematicClipboardText,
            "Be concise and accurate.",
            generatePanel.CustomInstruction,
            panelFormat: generatePanel.Format);

        var generatedMermaids = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var result = await conversationService.SendAsync(
                BaseUrl,
                model,
                [],
                prompt,
                maxTokens: 700,
                temperature: 0.2);
            var stripped = StripMermaidFence(result.AssistantText);
            generatedMermaids.Add(stripped);
            TestContext.Progress.WriteLine($"[Troubleshoot] Generated Mermaid {i + 1}: {stripped.Length} chars, issues: {string.Join(", ", DetectMermaidQualityIssues(stripped))}");
        }

        // Now try to repair each one with a synthetic error
        var repairResults = new List<(string Original, string Repaired, string? Error, List<string> OriginalIssues, List<string> RepairedIssues)>();
        foreach (var original in generatedMermaids)
        {
            var syntheticError = "Parse error: invalid syntax near line 1";
            var repair = await orchestrator.RepairMermaidDiagramAsync(
                BaseUrl,
                model,
                original,
                syntheticError,
                maxModelLength: 8192,
                cancellationToken: TestContext.CurrentContext.CancellationToken);

            var repairedIssues = DetectMermaidQualityIssues(repair.Text);
            repairResults.Add((original, repair.Text ?? string.Empty, repair.Error, DetectMermaidQualityIssues(original), repairedIssues));
            TestContext.Progress.WriteLine($"[Troubleshoot] Repair result: error={repair.Error}, issues={string.Join(", ", repairedIssues)}");
        }

        var builder = new StringBuilder();
        builder.AppendLine("=== Mermaid Repair Flow Analysis ===");
        builder.AppendLine($"Total repair attempts: {repairResults.Count}");
        builder.AppendLine($"Repair failures: {repairResults.Count(r => !string.IsNullOrWhiteSpace(r.Error))}");
        builder.AppendLine($"Repair improved issues: {repairResults.Count(r => r.OriginalIssues.Count > r.RepairedIssues.Count)}");
        builder.AppendLine($"Repair introduced issues: {repairResults.Count(r => r.RepairedIssues.Count > r.OriginalIssues.Count)}");
        builder.AppendLine($"Repair unchanged: {repairResults.Count(r => r.OriginalIssues.Count == r.RepairedIssues.Count)}");
        builder.AppendLine();
        for (int i = 0; i < repairResults.Count; i++)
        {
            var r = repairResults[i];
            builder.AppendLine($"--- Attempt {i + 1} ---");
            builder.AppendLine($"Original issues: {string.Join(", ", r.OriginalIssues)}");
            builder.AppendLine($"Repaired issues: {string.Join(", ", r.RepairedIssues)}");
            builder.AppendLine($"Repair error: {r.Error ?? "none"}");
            builder.AppendLine();
        }

        var report = builder.ToString();
        TestContext.Progress.WriteLine(report);
        TestContext.Out.WriteLine(report);

        Assert.Warn($"Repair flow complete. See test output for analysis.");
    }

    private static async Task<string> ResolveFirstModelIdAsync(HttpClient http, string baseUrl)
    {
        using var modelsResponse = await http.GetAsync($"{baseUrl.TrimEnd('/')}/v1/models");
        modelsResponse.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await modelsResponse.Content.ReadAsStringAsync());
        var data = payload.RootElement.GetProperty("data");
        Assert.That(data.GetArrayLength(), Is.GreaterThan(0), "vLLM returned no models from /v1/models.");

        var modelId = data[0].GetProperty("id").GetString();
        Assert.That(modelId, Is.Not.Null.And.Not.Empty, "First model id from /v1/models is empty.");
        return modelId!;
    }

    private sealed record PanelResult(
        string Name,
        string? Format,
        string? Text,
        string? Error,
        TimeSpan? GenerationDuration,
        int EstimatedVisibleTokenCount);

    private sealed record IterationResult(
        int Index,
        List<PanelResult> Panels,
        List<string> Errors,
        List<string> LogEntries);
}
