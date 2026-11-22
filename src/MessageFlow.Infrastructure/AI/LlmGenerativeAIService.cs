using System.Text;
using System.Text.Json;
using MessageFlow.Application.Abstractions;
using MessageFlow.Domain.AI;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Infrastructure.AI;

/// <summary>
/// Provider-agnostic implementation of IGenerativeAIService. It owns the prompts and the
/// validation of the model's answer; the actual call to Gemini, OpenAI or any other provider
/// is delegated to an <see cref="ILlmClient"/> (strategy), optionally wrapped by
/// <see cref="FallbackLlmClient"/>.
/// </summary>
public sealed class LlmGenerativeAIService(ILlmClient llmClient) : IGenerativeAIService
{
    public async Task<GeneratedMessageResult> GenerateMessageAsync(
        string description,
        string tone,
        IReadOnlyList<ColumnDefinition> columns,
        CancellationToken cancellationToken = default)
    {
        if (columns.Count == 0)
        {
            throw new DomainException("No dataset variables available for message generation.");
        }

        var prompt = BuildPrompt(description, tone, columns);
        var systemInstruction = BuildSystemInstruction();

        var text = await llmClient.CompleteAsync(systemInstruction, prompt, LlmOutputFormat.Json, cancellationToken);
        return ParseAndValidateResponse(text, columns);
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

    private static GeneratedMessageResult ParseAndValidateResponse(
        string text,
        IReadOnlyList<ColumnDefinition> columns)
    {
        var result = ParseJsonObject(text);

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
        if (columns.Count == 0)
        {
            throw new DomainException("No dataset columns available for query generation.");
        }

        var prompt = BuildQueryPrompt(question, columns);
        var systemInstruction = BuildQuerySystemInstruction();

        var text = await llmClient.CompleteAsync(systemInstruction, prompt, LlmOutputFormat.Json, cancellationToken);
        return ParseAndValidateQueryResponse(text, columns);
    }

    public async Task<string> GenerateResponseAsync(
        string originalQuestion,
        DatasetQuery query,
        object? queryData,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildResponsePrompt(originalQuestion, query, queryData);
        var systemInstruction = BuildResponseSystemInstruction();

        // Free-text answer: no JSON mode (these prompts do not ask for JSON).
        return await llmClient.CompleteAsync(systemInstruction, prompt, LlmOutputFormat.Text, cancellationToken);
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
        string text,
        IReadOnlyList<ColumnDefinition> columns)
    {
        var result = ParseJsonObject(text);

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
        sb.AppendLine("Result data (JSON):");
        sb.AppendLine(SerializeResultData(queryData));
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

    private static string SerializeResultData(object? queryData)
    {
        if (queryData is null)
        {
            return "null";
        }

        try
        {
            // ToString() sobre tipos anónimos y colecciones devuelve el nombre del tipo,
            // no los valores: el modelo se quedaba sin los datos que debía resumir.
            return JsonSerializer.Serialize(queryData);
        }
        catch (NotSupportedException)
        {
            return queryData.ToString() ?? string.Empty;
        }
    }

    public async Task<string> GenerateGroundedResponseAsync(
        string question,
        IReadOnlyList<GroundedRow> rows,
        IReadOnlyList<GroundedFilter> appliedFilters,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            throw new DomainException("Cannot answer without retrieved records.");
        }

        var prompt = BuildGroundedPrompt(question, rows, appliedFilters);
        var systemInstruction = BuildGroundedSystemInstruction();

        // Free-text answer: no JSON mode (these prompts do not ask for JSON).
        return await llmClient.CompleteAsync(systemInstruction, prompt, LlmOutputFormat.Text, cancellationToken);
    }

    private static string BuildGroundedPrompt(
        string question,
        IReadOnlyList<GroundedRow> rows,
        IReadOnlyList<GroundedFilter> appliedFilters)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Answer the user's question using ONLY the records listed below.");
        sb.AppendLine();
        sb.AppendLine($"User question: {question}");
        sb.AppendLine();

        if (appliedFilters.Count > 0)
        {
            sb.AppendLine("Filters applied to the retrieved records:");
            foreach (var filter in appliedFilters)
            {
                sb.AppendLine($"  - {filter.Column} {filter.Operator} {filter.Value ?? "(null)"}");
            }

            sb.AppendLine();
        }

        sb.AppendLine($"Retrieved records ({rows.Count}), ordered by semantic similarity:");
        sb.AppendLine();

        foreach (var row in rows)
        {
            sb.AppendLine($"- Row {row.RowNumber} (similarity {row.Similarity:0.000}):");
            foreach (var (column, value) in row.Values)
            {
                sb.AppendLine($"    {column}: {value ?? "(empty)"}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("RULES:");
        sb.AppendLine("1. Use only facts present in the records above. Never add outside knowledge.");
        sb.AppendLine("2. If the records do not contain the answer, say so plainly.");
        sb.AppendLine("3. Cite the row numbers you used so the user can verify them.");
        sb.AppendLine("4. Never invent rows, values or counts.");

        return sb.ToString();
    }

    private static string BuildGroundedSystemInstruction()
    {
        return """
            You answer questions about a dataset using only the records provided in the prompt.

            Rules:
            - The provided records are the ONLY admissible evidence.
            - Never use prior knowledge about the entities mentioned to fill gaps.
            - If the records do not answer the question, state that the dataset does not contain the answer.
            - Be concise and direct. No markdown, no preamble, no closing pleasantries.
            - Answer in the same language as the user's question.
            - Reference the row numbers you used so the answer is verifiable.
            - If the result is empty, say so.
            """;
    }

    /// <summary>
    /// The clients ask the provider for JSON, but a model can still return something else
    /// (or a JSON value that is not an object). Report that as a controlled error.
    /// </summary>
    private static JsonElement ParseJsonObject(string text)
    {
        try
        {
            var result = JsonSerializer.Deserialize<JsonElement>(text);

            if (result.ValueKind != JsonValueKind.Object)
            {
                throw new DomainException("AI service returned an unexpected response format.");
            }

            return result;
        }
        catch (JsonException)
        {
            throw new DomainException("AI service returned an invalid response format.");
        }
    }
}
