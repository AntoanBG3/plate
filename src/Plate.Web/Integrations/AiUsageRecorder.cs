using System.Diagnostics;
using System.Text.Json.Nodes;
using Plate.Core.Domain;
using Plate.Web.Data;

namespace Plate.Web.Integrations;

/// <summary>
/// Records each OpenRouter POST an account's request makes: the task, the model, tokens, cost, time and whether it
/// failed (SPEC 11). It sits in front of every AI client, so no integration has to remember to. OpenRouter puts the
/// cost in every answer's usage block. Calls without a signed-in user (the CLI's tests and evals) aren't recorded,
/// and recording never changes or fails the call.
/// </summary>
public sealed class AiUsageRecorder(string task, ICurrentUser currentUser, IServiceScopeFactory scopes, TimeProvider time, ILogger<AiUsageRecorder> log)
    : DelegatingHandler
{
    public static AiUsageRecorder For(IServiceProvider services, string task) =>
        ActivatorUtilities.CreateInstance<AiUsageRecorder>(services, task);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var userId = request.Method == HttpMethod.Post ? currentUser.UserId : null;
        if (userId is null)
        {
            return await base.SendAsync(request, ct);
        }

        var clock = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, ct);
            // Buffered, so the integration still reads the whole answer after this one has.
            await response.Content.LoadIntoBufferAsync(ct);
            return response;
        }
        finally
        {
            await RecordAsync(userId, request, response, (int)clock.ElapsedMilliseconds);
        }
    }

    private async Task RecordAsync(string userId, HttpRequestMessage request, HttpResponseMessage? response, int ms)
    {
        try
        {
            JsonNode? answer = null;
            if (response is { IsSuccessStatusCode: true })
            {
                try
                {
                    answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
                }
                catch (System.Text.Json.JsonException)
                {
                    // Not JSON: the call is recorded without usage.
                }
            }

            var usage = answer?["usage"] as JsonObject;
            var model = Text(answer?["model"]) ?? await RequestedModelAsync(request);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AiCalls.Add(new AiCall
            {
                UserId = userId,
                At = time.GetUtcNow(),
                Task = task,
                Model = model is { Length: > AiCall.MaxModelLength } ? model[..AiCall.MaxModelLength] : model,
                // Chat completions say prompt/completion tokens; the decisions API says input/output.
                InputTokens = Int(usage?["prompt_tokens"]) ?? Int(usage?["input_tokens"]),
                OutputTokens = Int(usage?["completion_tokens"]) ?? Int(usage?["output_tokens"]),
                Cost = usage?["cost"] is JsonValue c && c.TryGetValue<decimal>(out var cost) ? cost : null,
                DurationMs = ms,
                Failed = response is not { IsSuccessStatusCode: true },
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Couldn't record a {Task} call's usage.", task);
        }
    }

    private static async Task<string?> RequestedModelAsync(HttpRequestMessage request)
    {
        try
        {
            return request.Content is null ? null : Text(JsonNode.Parse(await request.Content.ReadAsStringAsync(CancellationToken.None))?["model"]);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
