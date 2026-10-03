using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.Integrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GymTracker.Web.Tests;

/// <summary>A fake OpenRouter: serves a small model list and answers chat completions with a fixed status and body.</summary>
internal sealed class StubOpenRouter(HttpStatusCode status = HttpStatusCode.OK, string? completion = null) : HttpMessageHandler
{
    public const string Banana = """
        {"choices":[{"message":{"role":"assistant","content":"{\"items\":[{\"nameEn\":\"banana\",\"nameOriginal\":\"1 banana\",\"grams\":120,\"state\":\"na\",\"preparation\":null,\"assumption\":null,\"kcalPer100g\":89,\"proteinPer100g\":1.1,\"carbsPer100g\":23,\"fatPer100g\":0.3}]}"}}]}
        """;

    public const string Models = """
        {"data":[
          {"id":"google/gemini-3.5-flash-lite","name":"Google: Gemini 3.5 Flash Lite","architecture":{"output_modalities":["text"]},
           "pricing":{"prompt":"0.0000003","completion":"0.0000025"},
           "supported_parameters":["reasoning","response_format","structured_outputs","temperature"],
           "reasoning":{"mandatory":true,"supported_efforts":["high","medium","low","minimal"]}},
          {"id":"openai/gpt-x","name":"OpenAI: GPT X","architecture":{"output_modalities":["text"]},
           "pricing":{"prompt":"0.000001","completion":"0.000004"},
           "supported_parameters":["reasoning","response_format","structured_outputs"],
           "reasoning":{"mandatory":false,"supported_efforts":["high","low","none"]}},
          {"id":"openai/gpt-x:batch","name":"OpenAI: GPT X (batch)","architecture":{"output_modalities":["text"]},
           "pricing":{"prompt":"0.0000005","completion":"0.000002"},"supported_parameters":["response_format","structured_outputs"]},
          {"id":"meta/plain","name":"Meta: Plain","architecture":{"output_modalities":["text"]},
           "pricing":{"prompt":"0","completion":"0"},"supported_parameters":["temperature"]},
          {"id":"google/image","name":"Google: Image","architecture":{"output_modalities":["image","text"]},
           "pricing":{"prompt":"0","completion":"0"},"supported_parameters":["response_format","structured_outputs"]},
          {"id":"openrouter/auto","name":"Auto Router","architecture":{"output_modalities":["text"]},
           "pricing":{"prompt":"-1","completion":"-1"},"supported_parameters":["response_format","structured_outputs"]}
        ]}
        """;

    public bool ModelsDown { get; set; }
    public string? SentKey { get; private set; }
    public JsonNode? SentBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/models"))
        {
            return ModelsDown ? throw new HttpRequestException("down") : Json(HttpStatusCode.OK, Models);
        }

        SentKey = request.Headers.Authorization?.Parameter;
        SentBody = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
        return Json(status, completion ?? Banana);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public OpenRouterModels Catalog() =>
        new(Client(), new MemoryCache(new MemoryCacheOptions()), NullLogger<OpenRouterModels>.Instance);

    public OpenRouterFoodParser Parser() => new(Client(), Catalog(), NullLogger<OpenRouterFoodParser>.Instance);

    private HttpClient Client() => new(this, disposeHandler: false) { BaseAddress = new Uri("https://openrouter.test/api/v1/") };
}

public class OpenRouterCatalogTests
{
    [Fact]
    public async Task Only_text_models_with_structured_output_and_a_fixed_price_are_listed()
    {
        var models = await new StubOpenRouter().Catalog().ListAsync(default);

        models!.Select(m => m.Id).Should().Equal("google/gemini-3.5-flash-lite", "openai/gpt-x");
        var gemini = models[0];
        gemini.Should().Match<OpenRouterModel>(m => m.Provider == "Google" && m.Name == "Gemini 3.5 Flash Lite"
            && m.PromptPerMillion == 0.3m && m.CompletionPerMillion == 2.5m && m.SupportsTemperature && m.LowestEffort == "minimal");
        gemini.Price.ToString().Should().Be("$0.30 in, $2.50 out per million tokens");
        models[1].LowestEffort.Should().Be("none", "reasoning is optional for that model");
    }

    [Fact]
    public async Task A_loaded_list_survives_OpenRouter_going_down_and_none_is_null()
    {
        var stub = new StubOpenRouter();
        var catalog = stub.Catalog();
        (await catalog.ListAsync(default)).Should().HaveCount(2);

        stub.ModelsDown = true;
        (await catalog.ListAsync(default)).Should().HaveCount(2);
        (await new StubOpenRouter { ModelsDown = true }.Catalog().ListAsync(default)).Should().BeNull();
    }
}

[Collection(PostgresCollection.Name)]
public class OpenRouterSettingsTests(PostgresFixture fixture)
{
    private const string SettingsKey = "sk-or-v1-settings-key-0123456789abcdWXYZ";
    private const string EnvironmentKey = "sk-or-v1-environment-key-0123456789ENVK";

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static OpenRouterSettings Store(UserScope s, string environmentKey = "", IDataProtectionProvider? protection = null, StubOpenRouter? stub = null, string model = "") =>
        new(s.Db, new FixedCurrentUser(s.UserId), protection ?? Protection,
            Options.Create(new OpenRouterOptions { ApiKey = environmentKey, Model = model }), (stub ?? new StubOpenRouter()).Catalog(),
            TimeProvider.System, NullLogger<OpenRouterSettings>.Instance);

    private static async Task ClearAsync(UserScope s) =>
        await s.Db.AppSettings.Where(x => x.Name.StartsWith("OpenRouter:")).ExecuteDeleteAsync();

    [Fact]
    public async Task A_key_saved_in_settings_wins_over_the_environment_and_is_stored_encrypted()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var store = Store(s, EnvironmentKey);
        try
        {
            (await store.KeyStatusAsync(default)).Should().Be(new OpenRouterKeyStatus(KeySource.Environment, "ENVK"));

            await store.SetKeyAsync($"  {SettingsKey} ", default);

            (await store.GetKeyAsync(default)).Should().Be(SettingsKey);
            (await store.KeyStatusAsync(default)).Should().Be(new OpenRouterKeyStatus(KeySource.Settings, "WXYZ"));
            var stored = await s.Db.AppSettings.AsNoTracking().SingleAsync(x => x.Name == OpenRouterSettings.KeySettingName);
            stored.Value.Should().NotContain("settings-key");
            stored.UpdatedByUserId.Should().Be(s.UserId);

            await store.ClearKeyAsync(default);
            (await store.GetKeyAsync(default)).Should().Be(EnvironmentKey);
        }
        finally
        {
            await ClearAsync(s);
        }
    }

    [Fact]
    public async Task A_key_that_can_no_longer_be_decrypted_is_ignored()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        try
        {
            await Store(s).SetKeyAsync(SettingsKey, default);

            // Different Data Protection keys, as after losing the keys folder.
            var afterKeyLoss = Store(s, protection: new EphemeralDataProtectionProvider());
            (await afterKeyLoss.KeyStatusAsync(default)).Should().Be(new OpenRouterKeyStatus(KeySource.None, null));
        }
        finally
        {
            await ClearAsync(s);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has a space in the middle of it ok")]
    public async Task Rejects_text_that_is_not_a_key(string key)
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        await Store(s).Invoking(x => x.SetKeyAsync(key, default)).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task The_model_comes_from_settings_then_the_environment_then_the_default()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        try
        {
            (await Store(s).GetModelAsync(default)).Should().Be(OpenRouterOptions.DefaultModel);
            (await Store(s, model: "openai/gpt-x").GetModelAsync(default)).Should().Be("openai/gpt-x");

            await Store(s).SetModelAsync("openai/gpt-x", default);
            (await Store(s, model: "google/gemini-3.5-flash-lite").GetModelAsync(default)).Should().Be("openai/gpt-x");
        }
        finally
        {
            await ClearAsync(s);
        }
    }

    [Theory]
    [InlineData("openai/gpt-x:batch")]
    [InlineData("meta/plain")]
    [InlineData("made/up")]
    public async Task Only_listed_models_can_be_picked(string id)
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        await Store(s).Invoking(x => x.SetModelAsync(id, default)).Should().ThrowAsync<ValidationException>();
        (await s.Db.AppSettings.AnyAsync(x => x.Name == OpenRouterSettings.ModelSettingName && x.Value == id)).Should().BeFalse();
    }

    [Fact]
    public async Task No_model_can_be_picked_while_the_list_is_unavailable()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        await Store(s, stub: new StubOpenRouter { ModelsDown = true }).Invoking(x => x.SetModelAsync("openai/gpt-x", default))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Without_any_key_the_built_in_parser_is_used()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var stub = new StubOpenRouter();
        var services = new ServiceCollection().AddSingleton(stub.Parser()).BuildServiceProvider();

        var withoutKey = await new SwitchingFoodParser(Store(s), services).ParseAsync("200g rice", default);
        withoutKey.Model.Should().Be(FakeFoodParser.ModelName);
        stub.SentKey.Should().BeNull();

        var withEnvironmentKey = await new SwitchingFoodParser(Store(s, EnvironmentKey), services).ParseAsync("1 banana", default);
        withEnvironmentKey.Items.Should().ContainSingle(i => i.NameEn == "banana");
        withEnvironmentKey.Model.Should().Be(OpenRouterOptions.DefaultModel);
        stub.SentKey.Should().Be(EnvironmentKey);
    }
}

public class OpenRouterParserTests
{
    [Fact]
    public async Task Sends_a_strict_schema_and_only_the_parameters_the_model_supports()
    {
        var stub = new StubOpenRouter();

        (await stub.Parser().TestAsync("sk-or-v1-key", "google/gemini-3.5-flash-lite", default)).Should().BeNull();

        stub.SentKey.Should().Be("sk-or-v1-key");
        var body = stub.SentBody!;
        body["model"]!.GetValue<string>().Should().Be("google/gemini-3.5-flash-lite");
        body["response_format"]!["type"]!.GetValue<string>().Should().Be("json_schema");
        body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>().Should().BeTrue();
        body["response_format"]!["json_schema"]!["schema"]!["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
        body["provider"]!["require_parameters"]!.GetValue<bool>().Should().BeTrue();
        body["reasoning"]!["effort"]!.GetValue<string>().Should().Be("minimal");
        body["temperature"].Should().NotBeNull();

        await stub.Parser().TestAsync("sk-or-v1-key", "openai/gpt-x", default);
        stub.SentBody!["temperature"].Should().BeNull("that model does not take a temperature");
        stub.SentBody!["reasoning"]!["effort"]!.GetValue<string>().Should().Be("none");

        await stub.Parser().TestAsync("sk-or-v1-key", "not/in-the-list", default);
        stub.SentBody!["temperature"].Should().BeNull();
        stub.SentBody!["reasoning"].Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected the API key (401)")]
    [InlineData(HttpStatusCode.PaymentRequired, "out of credits")]
    [InlineData(HttpStatusCode.NotFound, "Pick another one in Settings")]
    [InlineData(HttpStatusCode.TooManyRequests, "busy")]
    public async Task Errors_say_what_to_do(HttpStatusCode status, string expected)
    {
        var stub = new StubOpenRouter(status, """{"error":{"message":"nope"}}""");

        (await stub.Parser().TestAsync("sk-or-v1-key", "google/gemini-3.5-flash-lite", default))!.ToString().Should().Contain(expected);
    }
}
