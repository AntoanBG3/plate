using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using GymTracker.Core.Nutrition;
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
          {"id":"google/gemini-3.5-flash-lite","name":"Google: Gemini 3.5 Flash Lite","architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},
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

    /// <summary>Statuses for the next completions, in order; after them every call gets the constructor's status.</summary>
    public Queue<HttpStatusCode> Statuses { get; } = new();

    /// <summary>How long each completion takes to answer.</summary>
    public TimeSpan Delay { get; set; }

    public int Calls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/models"))
        {
            return ModelsDown ? throw new HttpRequestException("down") : Json(HttpStatusCode.OK, Models);
        }

        Calls++;
        SentKey = request.Headers.Authorization?.Parameter;
        SentBody = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        return Json(Statuses.Count > 0 ? Statuses.Dequeue() : status, completion ?? Banana);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public OpenRouterModels Catalog() =>
        new(Client(), new MemoryCache(new MemoryCacheOptions()), NullLogger<OpenRouterModels>.Instance);

    public OpenRouterFoodParser Parser() => new(Client(), Catalog(), NullLogger<OpenRouterFoodParser>.Instance);

    public OpenRouterOnboardingAssistant Onboarding() => new(Client(), Catalog(), NullLogger<OpenRouterOnboardingAssistant>.Instance);

    public OpenRouterFoodNamer Namer() => new(Client(), Catalog(), NullLogger<OpenRouterFoodNamer>.Instance);

    public OpenRouterLabelReader Label() => new(Client(), Catalog(), NullLogger<OpenRouterLabelReader>.Instance);

    /// <summary>A chat completion whose content is <paramref name="json"/>.</summary>
    public static string Completion(string json) =>
        new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = json } }) }.ToJsonString();

    private HttpClient Client() => new(this, disposeHandler: false) { BaseAddress = new Uri("https://openrouter.test/api/v1/") };
}

public class OpenRouterCatalogTests
{
    [Fact]
    public async Task Only_text_models_with_structured_output_and_a_fixed_price_are_listed()
    {
        var models = (await new StubOpenRouter().Catalog().ListAsync(default))!;

        models.Select(m => m.Id).Should().Equal("google/gemini-3.5-flash-lite", "openai/gpt-x");
        var gemini = models[0];
        gemini.Should().Match<OpenRouterModel>(m => m.Provider == "Google" && m.Name == "Gemini 3.5 Flash Lite"
            && m.PromptPerMillion == 0.3m && m.CompletionPerMillion == 2.5m && m.SupportsTemperature && m.LowestEffort == "minimal");
        gemini.Price.ToString().Should().Be("$0.30 in, $2.50 out per million tokens");
        models[1].LowestEffort.Should().Be("none", "reasoning is optional for that model");
    }

    [Fact]
    public async Task Models_that_accept_images_are_marked()
    {
        var models = (await new StubOpenRouter().Catalog().ListAsync(default))!;

        models.Single(m => m.Id == "google/gemini-3.5-flash-lite").AcceptsImages.Should().BeTrue();
        models.Single(m => m.Id == "openai/gpt-x").AcceptsImages.Should().BeFalse();
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
    public async Task The_label_picker_lists_only_vision_models_after_off()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        (await Store(s).ListModelsAsync(AiTask.Label, default))!.Select(m => m.Id).Should().Equal(AiTasks.Off, "google/gemini-3.5-flash-lite");
    }

    [Fact]
    public async Task The_label_model_defaults_to_gemini_flash_lite_and_not_the_parser()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await ClearAsync(s);

        (await Store(s, model: "openai/gpt-x").GetModelAsync(AiTask.Label, default)).Should().Be("google/gemini-3.5-flash-lite");
    }

    [Fact]
    public async Task A_text_only_model_is_refused_for_labels()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        try
        {
            var refused = await Store(s).Invoking(x => x.SetModelAsync(AiTask.Label, "openai/gpt-x", default)).Should().ThrowAsync<ValidationException>();
            refused.Which.Text.Should().Be(new GymTracker.Core.Text.LocalText("That model can't read labels."));

            await Store(s).SetModelAsync(AiTask.Label, AiTasks.Off, default);
            (await Store(s).GetModelAsync(AiTask.Label, default)).Should().Be(AiTasks.Off);
        }
        finally
        {
            await ClearAsync(s);
        }
    }

    [Fact]
    public async Task Each_task_has_its_own_model()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        try
        {
            (await Store(s).GetModelAsync(AiTask.Parser, default)).Should().Be(OpenRouterOptions.DefaultModel);
            (await Store(s, model: "openai/gpt-x").GetModelAsync(AiTask.Parser, default)).Should().Be("openai/gpt-x");
            (await Store(s).GetModelAsync(AiTask.Matcher, default)).Should().Be(AiTasks.Off);

            await Store(s).SetModelAsync(AiTask.Matcher, JevFoodMatchDecider.Jev113, default);
            (await Store(s).GetModelAsync(AiTask.Matcher, default)).Should().Be(JevFoodMatchDecider.Jev113);
            (await Store(s).GetModelAsync(AiTask.Parser, default)).Should().Be(OpenRouterOptions.DefaultModel, "the parser keeps its own model");

            // A model picked before the split is stored under the old name and keeps working for the parser.
            s.Db.AppSettings.Add(new AppSetting { Name = "OpenRouter:Model", Value = "openai/gpt-x", UpdatedAt = DateTimeOffset.UtcNow });
            await s.Db.SaveChangesAsync();
            (await Store(s, model: "google/gemini-3.5-flash-lite").GetModelAsync(AiTask.Parser, default)).Should().Be("openai/gpt-x");
        }
        finally
        {
            await ClearAsync(s);
        }
    }

    [Theory]
    [InlineData(AiTask.Parser, "openai/gpt-x:batch")]
    [InlineData(AiTask.Parser, "meta/plain")]
    [InlineData(AiTask.Parser, "made/up")]
    [InlineData(AiTask.Parser, JevFoodMatchDecider.Jev113)]
    [InlineData(AiTask.Matcher, "openai/gpt-x")]
    [InlineData(AiTask.Matcher, "~typesafe/jev-latest")]
    public async Task Only_listed_models_can_be_picked_per_task(AiTask task, string id)
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        await Store(s).Invoking(x => x.SetModelAsync(task, id, default)).Should().ThrowAsync<ValidationException>();
        (await s.Db.AppSettings.AnyAsync(x => x.Name == AiTasks.SettingName(task) && x.Value == id)).Should().BeFalse();
    }

    [Fact]
    public async Task The_matcher_runs_only_with_a_key_and_a_model()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var jev = new StubJev();
        var services = new ServiceCollection().AddSingleton(jev.Decider()).BuildServiceProvider();
        MatchQuestion[] banana = [new(new GymTracker.Core.Nutrition.ParsedFoodItem("banana", "banana", 120, GymTracker.Core.Nutrition.FoodState.NotApplicable, null, null, new GymTracker.Core.Nutrition.Per100g(89, 1, 23, 0)),
            [new MatchCandidate(1, "Bananas, raw", null, null, new GymTracker.Core.Nutrition.Per100g(89, 1, 23, 0))])];
        try
        {
            await Store(s).SetModelAsync(AiTask.Matcher, JevFoodMatchDecider.Jev113, default);
            (await new SwitchingMatchDecider(Store(s), services).DecideAsync("a banana", banana, default)).Should().BeNull("there is no key");
            jev.Calls.Should().Be(0);

            await Store(s).SetModelAsync(AiTask.Matcher, AiTasks.Off, default);
            (await new SwitchingMatchDecider(Store(s, EnvironmentKey), services).DecideAsync("a banana", banana, default)).Should().BeNull("the matcher is off");
            jev.Calls.Should().Be(0);

            await Store(s).SetModelAsync(AiTask.Matcher, JevFoodMatchDecider.Jev113, default);
            var decided = await new SwitchingMatchDecider(Store(s, EnvironmentKey), services).DecideAsync("a banana", banana, default);
            decided!.Items.Should().Equal(new MatchDecision(1, 0.97m));
            jev.Calls.Should().Be(1);
            jev.SentKey.Should().Be(EnvironmentKey);
        }
        finally
        {
            await ClearAsync(s);
        }
    }

    [Fact]
    public async Task No_model_can_be_picked_while_the_list_is_unavailable()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        await Store(s, stub: new StubOpenRouter { ModelsDown = true }).Invoking(x => x.SetModelAsync(AiTask.Parser, "openai/gpt-x", default))
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

public class OpenRouterNamerTests
{
    private static readonly NameRequest[] Two =
    [
        new("200g пилешко", "Chicken, broiler or fryers, breast, skinless, boneless, meat only, raw", null, FoodState.Raw),
        new("ориз", "Rice, white, long-grain, regular, enriched, cooked", null, FoodState.Cooked),
    ];

    [Fact]
    public async Task Sends_each_database_name_with_a_strict_schema()
    {
        var stub = new StubOpenRouter(completion: StubOpenRouter.Completion("""{"names":[{"index":0,"name":"Chicken breast","status":"raw"},{"index":1,"name":"White rice","status":null}]}"""));

        var names = await stub.Namer().NameAsync(Two, "sk-or-v1-key", "google/gemini-3.5-flash-lite", default);

        names.Should().Equal(new FoodName("Chicken breast", "raw"), new FoodName("White rice", null));
        stub.Calls.Should().Be(1);
        var body = stub.SentBody!;
        body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>().Should().BeTrue();
        body["provider"]!["require_parameters"]!.GetValue<bool>().Should().BeTrue();
        var sent = body["messages"]![1]!["content"]!.GetValue<string>();
        sent.Should().Contain("meat only, raw").And.Contain("200g пилешко");
    }

    [Fact]
    public async Task Names_follow_their_index_and_missing_ones_are_null()
    {
        var stub = new StubOpenRouter(completion: StubOpenRouter.Completion("""{"names":[{"index":1,"name":"White rice","status":"boiled"},{"index":7,"name":"stray","status":null}]}"""));

        (await stub.Namer().NameAsync(Two, "k", "openai/gpt-x", default)).Should().Equal(null, new FoodName("White rice", "boiled"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task A_failure_gives_no_names_and_no_retry(HttpStatusCode status, string content)
    {
        var stub = new StubOpenRouter(status, StubOpenRouter.Completion(content));

        (await stub.Namer().NameAsync(Two, "k", "openai/gpt-x", default)).Should().BeNull();
        stub.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_slow_answer_gives_no_names()
    {
        var stub = new StubOpenRouter(completion: StubOpenRouter.Completion("""{"names":[]}""")) { Delay = OpenRouterFoodNamer.Timeout + TimeSpan.FromSeconds(1) };

        (await stub.Namer().NameAsync(Two, "k", "openai/gpt-x", default)).Should().BeNull();
    }
}

public class OpenRouterLabelReaderTests
{
    private const string Ayran = """{"is_label":true,"name":"Айрян","brand":null,"basis":"per_100ml","serving_grams":null,"energy_kcal":38,"energy_kj":159,"protein":1.8,"carbs":2.6,"fat":2.2,"saturated_fat":1.4,"fiber":null}""";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0x01];

    [Fact]
    public async Task Sends_the_photo_as_a_data_url_with_a_strict_schema()
    {
        var stub = new StubOpenRouter(completion: StubOpenRouter.Completion(Ayran));

        await stub.Label().ReadAsync(Jpeg, "image/jpeg", "sk-test", "google/gemini-3.5-flash-lite", default);

        stub.SentKey.Should().Be("sk-test");
        var body = stub.SentBody!;
        body["model"]!.GetValue<string>().Should().Be("google/gemini-3.5-flash-lite");
        body["messages"]![1]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>().Should().StartWith("data:image/jpeg;base64,/9j/");
        body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>().Should().BeTrue();
        body["provider"]!["require_parameters"]!.GetValue<bool>().Should().BeTrue();
        body["reasoning"]!["effort"]!.GetValue<string>().Should().Be("minimal");
    }

    [Fact]
    public async Task Reads_the_answer_into_a_label_reading()
    {
        var result = await new StubOpenRouter(completion: StubOpenRouter.Completion(Ayran)).Label()
            .ReadAsync(Jpeg, "image/jpeg", "k", "google/gemini-3.5-flash-lite", default);

        result.Error.Should().BeNull();
        result.Model.Should().Be("google/gemini-3.5-flash-lite");
        result.Raw.Should().Be(Ayran);
        result.Reading.Should().Be(new LabelReading(true, "Айрян", null, LabelBasis.Per100ml, null, 38, 159, 1.8m, 2.6m, 2.2m, 1.4m, null));
    }

    [Fact]
    public async Task An_unreadable_answer_fails_and_keeps_what_came_back()
    {
        var result = await new StubOpenRouter(completion: StubOpenRouter.Completion("not json")).Label()
            .ReadAsync(Jpeg, "image/jpeg", "k", "google/gemini-3.5-flash-lite", default);

        result.Reading.Should().BeNull();
        result.Error.Should().Be(new GymTracker.Core.Text.LocalText("Couldn't read the label. Enter the values by hand."));
        result.Raw.Should().Be("not json");
    }

    [Fact]
    public async Task A_server_error_is_retried_once()
    {
        var stub = new StubOpenRouter(completion: StubOpenRouter.Completion(Ayran));
        stub.Statuses.Enqueue(HttpStatusCode.BadGateway);

        var result = await stub.Label().ReadAsync(Jpeg, "image/jpeg", "k", "google/gemini-3.5-flash-lite", default);

        result.Reading.Should().NotBeNull();
        stub.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_timeout_is_not_retried()
    {
        var stub = new StubOpenRouter(completion: StubOpenRouter.Completion(Ayran)) { Delay = TimeSpan.FromSeconds(5) };
        var reader = stub.Label();
        reader.AttemptTimeout = TimeSpan.FromMilliseconds(50);

        var result = await reader.ReadAsync(Jpeg, "image/jpeg", "k", "google/gemini-3.5-flash-lite", default);

        result.Error.Should().Be(new GymTracker.Core.Text.LocalText("Couldn't read the label. Enter the values by hand."));
        stub.Calls.Should().Be(1);
    }

    [Fact]
    public async Task The_admin_test_reads_the_bundled_sample_and_checks_its_energy()
    {
        const string sample = """{"is_label":true,"name":null,"brand":null,"basis":"per_100g","serving_grams":null,"energy_kcal":250,"energy_kj":1046,"protein":10,"carbs":30,"fat":10,"saturated_fat":4,"fiber":2}""";
        var right = new StubOpenRouter(completion: StubOpenRouter.Completion(sample));
        (await right.Label().TestAsync("k", "google/gemini-3.5-flash-lite", default)).Should().BeNull();
        right.SentBody!["messages"]![1]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>().Length.Should().BeGreaterThan(10_000, "the real sample photo is sent");

        var wrong = new StubOpenRouter(completion: StubOpenRouter.Completion(sample.Replace("\"energy_kcal\":250", "\"energy_kcal\":205")));
        (await wrong.Label().TestAsync("k", "google/gemini-3.5-flash-lite", default)).Should()
            .Be(new GymTracker.Core.Text.LocalText("The model misread the sample label (read {0} kcal, expected {1}).", "205", 250m));
    }
}

[Collection(PostgresCollection.Name)]
public class LabelReaderSwitchTests(PostgresFixture fixture)
{
    private static async Task<bool> AvailableAsync(AppFactory app)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ILabelReader>().IsAvailableAsync(default);
    }

    [Fact]
    public async Task Label_photos_need_a_key_and_a_label_model_that_isnt_off()
    {
        await using (var noKey = new AppFactory(fixture))
        {
            (await AvailableAsync(noKey)).Should().BeFalse();
        }

        await using var withKey = new AppFactory(fixture, settings: new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "sk-or-v1-label-switch-test-0123456789" });
        await using var db = fixture.CreateContext(null);
        await db.AppSettings.Where(x => x.Name == "OpenRouter:Model:Label").ExecuteDeleteAsync();
        try
        {
            (await AvailableAsync(withKey)).Should().BeTrue("the default label model is on");

            db.AppSettings.Add(new AppSetting { Name = "OpenRouter:Model:Label", Value = AiTasks.Off, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            (await AvailableAsync(withKey)).Should().BeFalse();
        }
        finally
        {
            await db.AppSettings.Where(x => x.Name == "OpenRouter:Model:Label").ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task A_read_that_cannot_run_names_the_configured_model()
    {
        await using var db = fixture.CreateContext(null);
        await db.AppSettings.Where(x => x.Name == "OpenRouter:Model:Label").ExecuteDeleteAsync();
        await using var noKey = new AppFactory(fixture);
        using var scope = noKey.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ILabelReader>().ReadAsync([0xFF, 0xD8, 0xFF], "image/jpeg", default);

        (result.Error, result.Model).Should().Be((new GymTracker.Core.Text.LocalText("Couldn't read the label. Enter the values by hand."), AiTasks.DefaultLabelModel));
    }
}
