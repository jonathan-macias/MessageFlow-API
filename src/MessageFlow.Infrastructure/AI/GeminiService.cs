using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MessageFlow.Application.Abstractions;
using MessageFlow.Domain.AI;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace MessageFlow.Infrastructure.AI;

/// <summary>
/// Gemini API configuration. Bound from appsettings.json "Gemini" section.
/// </summary>
public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    /// <summary>Gemini API key. Never exposed to frontend.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Gemini model to use (e.g., "gemini-2.5-flash-lite"). Configurable without code changes.</summary>
    public string Model { get; set; } = "gemini-2.5-flash-lite";
}

/// <summary>
/// Gemini API implementation of IGenerativeAIService.
/// Generates WhatsApp message templates using dataset variables via Google's Gemini API.
/// </summary>
public sealed class GeminiService(
    HttpClient httpClient,
    GeminiOptions options,
    ILogger<GeminiService> logger) : IGenerativeAIService
{
    private const int TimeoutSeconds = 60;

    public async Task<GeneratedMessageResult> GenerateMessageAsync(
        string description,
        string tone,
        IReadOnlyList<ColumnDefinition> columns,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new DomainException("Gemini API key is not configured.");
        }

        if (columns.Count == 0)
        {
            throw new DomainException("No dataset variables available for message generation.");
        }

        var prompt = BuildPrompt(description, tone, columns);
        var systemInstruction = BuildSystemInstruction();

        try
        {
            var response = await CallGeminiApiAsync(prompt, systemInstruction, cancellationToken);
            return ParseAndValidateResponse(response, columns);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Failed to call Gemini API.");
            throw new DomainException($"Failed to communicate with AI service: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("Gemini API request timed out.");
            throw new DomainException("AI service request timed out. Please try again.");
        }
    }

    private static string BuildPrompt(string description, string tone, IReadOnlyList<ColumnDefinition> columns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Generate a WhatsApp message template based on the following request:");
        sb.AppendLine();
        sb.AppendLine($"Description: {description}");
        sb.AppendLine($"Tone: {tone}");
        sb.AppendLine();
        sb.AppendLine("Available dataset variables:");
        sb.AppendLine();

        foreach (var column in columns)
        {
            sb.AppendLine($"- Id: {column.Id}");
            sb.AppendLine($"  Name: {column.Name}");
            sb.AppendLine($"  DataType: {(int)column.DataType}");
            sb.AppendLine();
        }

        sb.AppendLine("IMPORTANT RULES:");
        sb.AppendLine("1. Use ONLY the variables listed above.");
        sb.AppendLine("2. Variables must use the syntax: {{variableId}}");
        sb.AppendLine("3. The value inside {{}} must be the exact Id of an available dataset variable.");
        sb.AppendLine("4. Use the variable Name only to understand its semantic meaning.");
        sb.AppendLine("5. Never use the variable Name itself inside {{}}.");
        sb.AppendLine("6. Do not invent variables that are not in the list.");
        sb.AppendLine("7. Do not include the phone number variable unless explicitly requested.");
        sb.AppendLine("8. Return ONLY a JSON object with this structure:");
        sb.AppendLine("   {\"messageTemplate\": \"your message here\", \"variableIds\": [\"id1\", \"id2\"]}");

        return sb.ToString();
    }

    private static string BuildSystemInstruction()
    {
        return """
            You are a WhatsApp message template generator.

            Generate a useful, natural, concise and professional WhatsApp message.

            Use ONLY variables provided in the available dataset variables.
            Variables must use exactly this syntax: {{variableId}}
            The value inside {{ }} must always be the exact ID of an available dataset variable.
            Use the variable name only to understand its semantic meaning.
            Never use the variable name itself inside {{ }}.
            Do not invent variables.
            Do not infer variables that are not explicitly provided.
            Do not rename variables.
            Do not create IDs.
            Do not modify variable IDs.
            If the requested message requires information that does not exist in the dataset, generate the best possible message using the available variables.
            Do not include the phone number variable in the message unless explicitly requested.
            The generated message must be suitable for WhatsApp.
            Keep the message concise and natural.
            Do not add explanations, markdown or comments around the generated message.
            Return only structured data according to the requested response schema.
            Return only the variables actually used in the generated message.
            """;
    }

    private async Task<JsonElement> CallGeminiApiAsync(
        string prompt,
        string systemInstruction,
        CancellationToken cancellationToken)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{options.Model}:generateContent";

        var requestBody = new
        {
            system_instruction = new
            {
                parts = new[] { new { text = systemInstruction } }
            },
            contents = new[]
            {
                new
                {
                    parts = new[] { new { text = prompt } }
                }
            },
            generation_config = new
            {
                response_mime_type = "application/json",
                temperature = 0.7,
                max_output_tokens = 1024
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(requestBody, options: new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            })
        };

        // Use x-goog-api-key header instead of query parameter
        request.Headers.Add("x-goog-api-key", options.ApiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("Gemini API returned {StatusCode}: {Error}", response.StatusCode, errorContent);
            throw new DomainException($"AI service returned {(int)response.StatusCode}: {errorContent}");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json;
    }

    private static GeneratedMessageResult ParseAndValidateResponse(
        JsonElement response,
        IReadOnlyList<ColumnDefinition> columns)
    {
        // Extract text from Gemini response structure
        if (!response.TryGetProperty("candidates", out var candidates) ||
            candidates.GetArrayLength() == 0)
        {
            throw new DomainException("AI service returned an empty response.");
        }

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.GetArrayLength() == 0)
        {
            throw new DomainException("AI service returned an invalid response structure.");
        }

        var text = parts[0].GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DomainException("AI service returned an empty message.");
        }

        // Parse the JSON response from Gemini
        var result = JsonSerializer.Deserialize<JsonElement>(text);

        if (!result.TryGetProperty("messageTemplate", out var messageTemplateElement))
        {
            throw new DomainException("AI response missing 'messageTemplate' field.");
        }

        var messageTemplate = messageTemplateElement.GetString();
        if (string.IsNullOrWhiteSpace(messageTemplate))
        {
            throw new DomainException("AI generated an empty message template.");
        }

        // Extract variable IDs from the message template
        var variableIdsFromTemplate = ExtractVariableIds(messageTemplate);

        // Validate all variable IDs exist in the dataset
        var validColumnIds = columns.Select(c => c.Id).ToHashSet();
        var invalidIds = variableIdsFromTemplate.Where(id => !validColumnIds.Contains(id)).ToList();

        if (invalidIds.Count > 0)
        {
            throw new DomainException(
                $"AI generated template with invalid variable IDs: {string.Join(", ", invalidIds)}. " +
                "These variables do not exist in the selected dataset.");
        }

        // Get the actual variables used
        var usedColumns = columns.Where(c => variableIdsFromTemplate.Contains(c.Id)).ToList();
        var usedVariableIds = usedColumns.Select(c => c.Id).ToList();

        return new GeneratedMessageResult(messageTemplate, usedVariableIds);
    }

    private static List<Guid> ExtractVariableIds(string template)
    {
        var ids = new List<Guid>();
        var regex = new System.Text.RegularExpressions.Regex(@"\{\{\s*([0-9a-fA-F-]+)\s*\}\}");

        foreach (System.Text.RegularExpressions.Match match in regex.Matches(template))
        {
            if (Guid.TryParse(match.Groups[1].Value, out var id))
            {
                ids.Add(id);
            }
        }

        return ids.Distinct().ToList();
    }

    public async Task<DatasetQuery> GenerateDatasetQueryAsync(
        string question,
        IReadOnlyList<ColumnDefinition> columns,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new DomainException("Gemini API key is not configured.");
        }

        if (columns.Count == 0)
        {
            throw new DomainException("No dataset columns available for query generation.");
        }

        var prompt = BuildQueryPrompt(question, columns);
        var systemInstruction = BuildQuerySystemInstruction();

        try
        {
            var response = await CallGeminiApiAsync(prompt, systemInstruction, cancellationToken);
            return ParseAndValidateQueryResponse(response, columns);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Failed to call Gemini API for dataset query.");
            throw new DomainException($"Failed to communicate with AI service: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("Gemini API request timed out for dataset query.");
            throw new DomainException("AI service request timed out. Please try again.");
        }
    }

    public async Task<string> GenerateResponseAsync(
        string originalQuestion,
        DatasetQuery query,
        object? queryData,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new DomainException("Gemini API key is not configured.");
        }

        var prompt = BuildResponsePrompt(originalQuestion, query, queryData);
        var systemInstruction = BuildResponseSystemInstruction();

        try
        {
            var response = await CallGeminiApiAsync(prompt, systemInstruction, cancellationToken);
            return ExtractTextFromResponse(response);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Failed to call Gemini API for response generation.");
            throw new DomainException($"Failed to communicate with AI service: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("Gemini API request timed out for response generation.");
            throw new DomainException("AI service request timed out. Please try again.");
        }
    }

    private static string BuildQueryPrompt(string question, IReadOnlyList<ColumnDefinition> columns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("The user wants to query a dataset. Convert their natural language question into a structured query.");
        sb.AppendLine();
        sb.AppendLine($"User question: {question}");
        sb.AppendLine();
        sb.AppendLine("Available dataset columns:");
        sb.AppendLine();

        foreach (var column in columns)
        {
            sb.AppendLine($"- Name: {column.Name}");
            sb.AppendLine($"  DataType: {(int)column.DataType} ({column.DataType})");
            sb.AppendLine();
        }

        sb.AppendLine("Available actions:");
        sb.AppendLine("- count: Count rows matching filters");
        sb.AppendLine("- sum: Sum a numeric column (requires targetColumn)");
        sb.AppendLine("- average: Average a numeric column (requires targetColumn)");
        sb.AppendLine("- min: Minimum value of a column (requires targetColumn)");
        sb.AppendLine("- max: Maximum value of a column (requires targetColumn)");
        sb.AppendLine("- group_by: Group by a column and count (requires groupByColumn)");
        sb.AppendLine("- top: Top N rows by a column (requires targetColumn and limit)");
        sb.AppendLine("- search: Search rows matching filters and return results");
        sb.AppendLine();
        sb.AppendLine("Available filter operators:");
        sb.AppendLine("- equals, not_equals, contains, greater_than, greater_than_or_equal, less_than, less_than_or_equal, is_null, is_not_null");
        sb.AppendLine();
        sb.AppendLine("IMPORTANT RULES:");
        sb.AppendLine("1. Use ONLY column names that exist in the dataset.");
        sb.AppendLine("2. Match the user's intent to the appropriate action.");
        sb.AppendLine("3. For filters, use the exact column names from the dataset.");
        sb.AppendLine("4. Return ONLY a JSON object with this structure:");
        sb.AppendLine("   {");
        sb.AppendLine("     \"action\": \"count|sum|average|min|max|group_by|top|search\",");
        sb.AppendLine("     \"targetColumn\": \"columnName or null\",");
        sb.AppendLine("     \"groupByColumn\": \"columnName or null\",");
        sb.AppendLine("     \"limit\": number or null,");
        sb.AppendLine("     \"filters\": [");
        sb.AppendLine("       { \"column\": \"columnName\", \"operator\": \"operator\", \"value\": \"value or null\" }");
        sb.AppendLine("     ]");
        sb.AppendLine("   }");
        sb.AppendLine("5. If the question is ambiguous, choose the most likely interpretation.");
        sb.AppendLine("6. If the question cannot be answered with the available columns, return an error action.");

        return sb.ToString();
    }

    private static string BuildQuerySystemInstruction()
    {
        return """
            You are a dataset query generator. Your job is to convert natural language questions into structured queries.

            You must analyze the user's question and the available dataset columns to generate a precise query.

            Rules:
            - Use ONLY column names that are explicitly provided in the available columns.
            - Never invent or assume column names.
            - Match the user's intent to the most appropriate action.
            - For questions about counts, use "count" action.
            - For questions about sums or totals, use "sum" action with the appropriate numeric column.
            - For questions about averages, use "average" action.
            - For questions about minimums or maximums, use "min" or "max" actions.
            - For questions about grouping (e.g., "by city"), use "group_by" action.
            - For questions about top N items, use "top" action with a limit.
            - For questions that need to see data, use "search" action.
            - When filtering, use the correct operator based on the user's words.
            - "more than" = greater_than, "less than" = less_than, "equal to" = equals, etc.
            - Return ONLY valid JSON. No explanations, no markdown.
            """;
    }

    private static DatasetQuery ParseAndValidateQueryResponse(
        JsonElement response,
        IReadOnlyList<ColumnDefinition> columns)
    {
        var text = ExtractTextFromResponse(response);
        var result = JsonSerializer.Deserialize<JsonElement>(text);

        // Parse action
        if (!result.TryGetProperty("action", out var actionElement))
        {
            throw new DomainException("AI response missing 'action' field.");
        }

        var actionStr = actionElement.GetString();
        if (!Enum.TryParse<DatasetQueryAction>(actionStr, true, out var action))
        {
            throw new DomainException($"Invalid action: '{actionStr}'.");
        }

        // Parse target column
        string? targetColumn = null;
        if (result.TryGetProperty("targetColumn", out var targetColumnElement) &&
            targetColumnElement.ValueKind != JsonValueKind.Null)
        {
            targetColumn = targetColumnElement.GetString();
        }

        // Parse groupBy column
        string? groupByColumn = null;
        if (result.TryGetProperty("groupByColumn", out var groupByElement) &&
            groupByElement.ValueKind != JsonValueKind.Null)
        {
            groupByColumn = groupByElement.GetString();
        }

        // Parse limit
        int? limit = null;
        if (result.TryGetProperty("limit", out var limitElement) &&
            limitElement.ValueKind != JsonValueKind.Null)
        {
            limit = limitElement.GetInt32();
        }

        // Parse filters
        var filters = new List<DatasetQueryFilter>();
        if (result.TryGetProperty("filters", out var filtersElement) &&
            filtersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var filterElement in filtersElement.EnumerateArray())
            {
                var column = filterElement.GetProperty("column").GetString()!;
                var operatorStr = filterElement.GetProperty("operator").GetString()!;
                string? value = null;
                if (filterElement.TryGetProperty("value", out var valueElement) &&
                    valueElement.ValueKind != JsonValueKind.Null)
                {
                    value = valueElement.GetString();
                }

                if (!Enum.TryParse<DatasetQueryOperator>(operatorStr, true, out var op))
                {
                    throw new DomainException($"Invalid filter operator: '{operatorStr}'.");
                }

                filters.Add(DatasetQueryFilter.Create(column, op, value));
            }
        }

        // Validate columns exist
        var validColumnNames = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(targetColumn) && !validColumnNames.Contains(targetColumn))
        {
            throw new DomainException($"Column '{targetColumn}' does not exist in the dataset.");
        }

        if (!string.IsNullOrWhiteSpace(groupByColumn) && !validColumnNames.Contains(groupByColumn))
        {
            throw new DomainException($"Column '{groupByColumn}' does not exist in the dataset.");
        }

        foreach (var filter in filters)
        {
            if (!validColumnNames.Contains(filter.Column))
            {
                throw new DomainException($"Filter column '{filter.Column}' does not exist in the dataset.");
            }
        }

        return DatasetQuery.Create(action, targetColumn, filters, groupByColumn, limit);
    }

    private static string BuildResponsePrompt(string originalQuestion, DatasetQuery query, object? queryData)
    {
        var sb = new StringBuilder();
        sb.AppendLine("The user asked a question about their dataset.");
        sb.AppendLine();
        sb.AppendLine($"Original question: {originalQuestion}");
        sb.AppendLine();
        sb.AppendLine("The query was executed and returned the following result:");
        sb.AppendLine();
        sb.AppendLine($"Query action: {query.Action}");

        if (!string.IsNullOrWhiteSpace(query.TargetColumn))
        {
            sb.AppendLine($"Target column: {query.TargetColumn}");
        }

        if (!string.IsNullOrWhiteSpace(query.GroupByColumn))
        {
            sb.AppendLine($"Group by: {query.GroupByColumn}");
        }

        if (query.Filters.Count > 0)
        {
            sb.AppendLine("Filters:");
            foreach (var filter in query.Filters)
            {
                sb.AppendLine($"  - {filter.Column} {filter.Operator} {filter.Value}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Result data:");
        sb.AppendLine(queryData?.ToString() ?? "No data");
        sb.AppendLine();
        sb.AppendLine("Generate a clear, concise, natural language response in the same language as the user's question.");
        sb.AppendLine("Include the specific numbers and data in your response.");
        sb.AppendLine("Do not add explanations about how the query was executed.");
        sb.AppendLine("Just answer the user's question directly.");

        return sb.ToString();
    }

    private static string BuildResponseSystemInstruction()
    {
        return """
            You are a helpful data analyst. Your job is to answer the user's question based on the query results.

            Rules:
            - Answer in the same language as the user's question.
            - Be clear, concise, and direct.
            - Include specific numbers and data from the results.
            - Do not explain how the query was executed.
            - Do not add technical details.
            - Just answer the question naturally.
            - If the result is empty, say "No results found" or equivalent.
            - Format numbers appropriately (e.g., 1,245 instead of 1245).
            """;
    }

    private static string ExtractTextFromResponse(JsonElement response)
    {
        if (!response.TryGetProperty("candidates", out var candidates) ||
            candidates.GetArrayLength() == 0)
        {
            throw new DomainException("AI service returned an empty response.");
        }

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.GetArrayLength() == 0)
        {
            throw new DomainException("AI service returned an invalid response structure.");
        }

        var text = parts[0].GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DomainException("AI service returned an empty message.");
        }

        return text;
    }
}
