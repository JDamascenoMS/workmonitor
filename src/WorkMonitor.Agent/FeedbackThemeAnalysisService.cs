using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkMonitor.Core.Clients;
using WorkMonitor.Core.Helpers;
using WorkMonitor.Core.Services;

namespace WorkMonitor.Agent;

public interface IFeedbackThemeClassifier
{
    Task<string> ClassifyFeedbackThemes(string feedbackJson, CancellationToken cancellationToken = default);
}

public sealed record FeedbackTheme(string Theme, string Description, IReadOnlyList<int> WorkItemIds, int Count);

public sealed record FeedbackThemeItem(
    int Id, string Title, string State, int Priority, string AssignedTo, string CreatedDate, string Url);

public sealed record FeedbackThemeSource(
    string Kind, string? ProjectArea, string? AreaPath, string? Query, string? QueryId,
    string Selection, int MaxResults, int ObservedCount, int? TotalCount, bool Truncated,
    int SelectedCount, int RetrievedCount, IReadOnlyList<int> MissingWorkItemIds,
    IReadOnlyList<int> ExcludedNonFeedbackWorkItemIds);

public sealed record FeedbackThemeCoverage(
    int AnalyzedCount, int ClassifiedCount, IReadOnlyList<int> UnclassifiedWorkItemIds,
    IReadOnlyList<int> PayloadOmittedWorkItemIds, IReadOnlyList<int> TextTruncatedWorkItemIds,
    bool IsPartial, bool OutputTruncated, IReadOnlyList<int> OutputOmittedWorkItemIds);

public sealed record FeedbackThemeAnalysisResult(
    IReadOnlyList<FeedbackTheme> Themes, IReadOnlyList<FeedbackThemeItem> Items,
    FeedbackThemeSource Source, FeedbackThemeCoverage Coverage)
{
    public FeedbackThemeAnalysisResult FitForTool(int maxCharacters = ReadResultBudget.MaxCharacters)
        => ReadResultBudget.FitCollection(Items, (retained, truncated) =>
        {
            var ids = retained.Select(item => item.Id).ToHashSet();
            return this with
            {
                Items = retained,
                Themes = Themes.Select(theme => theme with
                {
                    WorkItemIds = theme.WorkItemIds.Where(ids.Contains).ToArray(),
                    Count = theme.WorkItemIds.Count(ids.Contains)
                }).Where(theme => theme.Count > 0).ToArray(),
                Coverage = Coverage with
                {
                    OutputTruncated = truncated,
                    OutputOmittedWorkItemIds = Items.Where(item => !ids.Contains(item.Id)).Select(item => item.Id).ToArray(),
                    IsPartial = Coverage.IsPartial || truncated
                }
            };
        }, maxCharacters);
}

public sealed record FeedbackThemeRequest(string? ProjectArea, string? AreaPath, string? Query, string? QueryId, int MaxResults);

public sealed class FeedbackThemeAnalysisService
{
    internal const int MaxPayloadCharacters = 100000;
    internal const int MaxModelOutputCharacters = 100000;
    private readonly ParametersService _parameters;
    private readonly Func<IFeedbackThemeClassifier> _classifier;

    public FeedbackThemeAnalysisService(ParametersService parameters, Func<IFeedbackThemeClassifier> classifier)
    {
        _parameters = parameters;
        _classifier = classifier;
    }

    public FeedbackThemeRequest CreateRequest(string? projectArea = null, string? query = null, int maxResults = 20)
    {
        if (maxResults is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(maxResults), "maxResults must be between 1 and 200.");
        if (projectArea != null && query != null)
            throw new ArgumentException("Specify projectArea or query, not both.");
        if (query != null)
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length > 256)
                throw new ArgumentException("query must be a saved-query name or GUID of at most 256 characters.");
            var resolved = _parameters.ResolveSavedQuery(query);
            return new FeedbackThemeRequest(null, null, resolved.Name, resolved.QueryId, maxResults);
        }

        projectArea ??= "Project";
        if (string.IsNullOrWhiteSpace(projectArea) || projectArea.Length > 256 ||
            !_parameters.GetAreaPaths().TryGetValue(projectArea, out var areaPath))
            throw new ArgumentException("Unknown projectArea. Call list_project_areas for configured keys.");
        return new FeedbackThemeRequest(projectArea, areaPath, null, null, maxResults);
    }

    public async Task<FeedbackThemeAnalysisResult> AnalyzeAsync(
        MonitorClient client, FeedbackThemeRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = await client.GetFeedbackThemeCandidates(
            request.AreaPath, request.QueryId, request.MaxResults, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var eligible = window.Items.Where(item =>
            item.fields?.WorkItemType?.Equals("FeedbackTicket", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        var missing = window.SelectedIds.Except(window.Items.Select(item => item.id)).ToArray();
        var excluded = window.Items.Except(eligible).Select(item => item.id).ToArray();
        var payloadItems = new List<ClassifierItem>();
        var items = new List<FeedbackThemeItem>();
        var textTruncated = new List<int>();
        foreach (var item in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = item.fields;
            var payloadItem = new ClassifierItem(item.id, Clip(fields.Title, 256),
                Clip(fields.Description, 2000), Clip(fields.State, 80), fields.Priority);
            payloadItems.Add(payloadItem);
            if (JsonSerializer.Serialize(payloadItems).Length > MaxPayloadCharacters)
            {
                payloadItems.RemoveAt(payloadItems.Count - 1);
                break;
            }

            if (payloadItem.Title != (fields.Title ?? "") ||
                payloadItem.Description != (fields.Description ?? "") || payloadItem.State != (fields.State ?? ""))
                textTruncated.Add(item.id);
            items.Add(new FeedbackThemeItem(
                item.id, payloadItem.Title, payloadItem.State, fields.Priority,
                Clip(fields.AssignedTo?.displayName ?? "Unassigned", 128), Clip(fields.CreatedDate, 64),
                MonitorClient.WorkItemBrowserUrl(item.id)));
        }

        IReadOnlyList<FeedbackTheme> themes = [];
        if (items.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string raw;
            try
            {
                raw = await _classifier().ClassifyFeedbackThemes(
                    JsonSerializer.Serialize(payloadItems), cancellationToken);
            }
            catch (ClientResultException ex) when (ex.Status is 401 or 403)
            {
                throw new InvalidOperationException(
                    "Azure OpenAI denied theme analysis. Verify AzureOpenAI:Endpoint and DeploymentName, " +
                    "and grant the saved identity Cognitive Services OpenAI User on that resource " +
                    "or configure its AzureOpenAI:ApiKey.", ex);
            }
            cancellationToken.ThrowIfCancellationRequested();
            themes = ParseThemes(raw, items.Select(item => item.Id).ToHashSet());
        }

        var analyzedIds = items.Select(item => item.Id).ToHashSet();
        var classifiedIds = themes.SelectMany(theme => theme.WorkItemIds).ToHashSet();
        var unclassified = items.Where(item => !classifiedIds.Contains(item.Id)).Select(item => item.Id).ToArray();
        var omitted = eligible.Where(item => !analyzedIds.Contains(item.id)).Select(item => item.id).ToArray();
        var source = new FeedbackThemeSource(
            request.QueryId == null ? "area" : "savedQuery", request.ProjectArea, request.AreaPath,
            request.Query, request.QueryId,
            request.QueryId == null ? "Active feedback; created date descending, then priority ascending" : "Saved-query order; FeedbackTicket items only within the selected window",
            request.MaxResults, window.ObservedCount, window.TotalCount, window.Truncated,
            window.SelectedIds.Count, window.Items.Count, missing, excluded);
        return new FeedbackThemeAnalysisResult(themes, items, source, new FeedbackThemeCoverage(
            items.Count, classifiedIds.Count, unclassified, omitted, textTruncated,
            window.Truncated || missing.Length > 0 || excluded.Length > 0 ||
                omitted.Length > 0 || textTruncated.Count > 0 || unclassified.Length > 0,
            false, []));
    }

    internal static IReadOnlyList<FeedbackTheme> ParseThemes(string raw, HashSet<int> sourceIds)
    {
        if (raw.Length > MaxModelOutputCharacters)
            throw new InvalidOperationException("Theme model output exceeded the response limit.");
        var json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = json.IndexOf('\n');
            var language = newline < 0 ? "" : json[3..newline].Trim();
            if (newline < 0 || (language.Length > 0 && !language.Equals("json", StringComparison.OrdinalIgnoreCase)) ||
                !json.EndsWith("```", StringComparison.Ordinal))
                throw InvalidThemes();
            json = json[(newline + 1)..^3].Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > sourceIds.Count)
                throw InvalidThemes();
            var result = new List<FeedbackTheme>();
            foreach (var value in document.RootElement.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 4 ||
                    !value.TryGetProperty("theme", out var name) || name.ValueKind != JsonValueKind.String ||
                    !value.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String ||
                    !value.TryGetProperty("workItemIds", out var ids) || ids.ValueKind != JsonValueKind.Array ||
                    !value.TryGetProperty("count", out var count) || count.ValueKind != JsonValueKind.Number ||
                    !count.TryGetInt32(out var reportedCount))
                    throw InvalidThemes();
                if (string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length > 100 ||
                    string.IsNullOrWhiteSpace(description.GetString()) || description.GetString()!.Length > 400 ||
                    ids.GetArrayLength() == 0 || ids.GetArrayLength() > sourceIds.Count)
                    throw InvalidThemes();
                var matching = new HashSet<int>();
                foreach (var id in ids.EnumerateArray())
                {
                    if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) ||
                        !sourceIds.Contains(number) || !matching.Add(number))
                        throw InvalidThemes();
                }

                if (reportedCount != matching.Count)
                    throw new InvalidOperationException("Theme model count does not match its source work-item ids.");
                result.Add(new FeedbackTheme(name.GetString()!, description.GetString()!, matching.ToArray(), matching.Count));
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Theme model returned invalid JSON.", ex);
        }
    }

    private static InvalidOperationException InvalidThemes()
        => new("Theme model returned malformed data or work-item ids outside the analyzed source.");

    private static string Clip(string? value, int limit)
        => string.IsNullOrEmpty(value) ? "" : value.Length <= limit ? value : value[..limit];

    private sealed record ClassifierItem(int Id, string Title, string Description, string State, int Priority);
}

public static class FeedbackThemeServiceRegistration
{
    public static IServiceCollection AddFeedbackThemeAnalysis(this IServiceCollection services)
    {
        services.TryAddSingleton<AzureOpenAIService>();
        services.TryAddSingleton<IFeedbackThemeClassifier>(provider => provider.GetRequiredService<AzureOpenAIService>());
        services.AddSingleton(provider => new FeedbackThemeAnalysisService(
            provider.GetRequiredService<ParametersService>(),
            () => provider.GetRequiredService<IFeedbackThemeClassifier>()));
        return services;
    }
}
