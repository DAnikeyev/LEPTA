using LEPTA.Controllers.Models;
using LEPTA.vLLM.Models;

namespace LEPTA.Tests;

[TestFixture]
public sealed class ServerProfileFormMapperTests
{
    [Test]
    public void Apply_WritesAllSuppliedFieldsToServer()
    {
        var server = new VllmServerConfiguration();
        var state = new ServerProfileFormState
        {
            Name = "OpenRouter",
            UseExistingHttpServer = true,
            HttpServerAddress = "https://openrouter.ai/api/v1",
            Model = "anthropic/claude-haiku",
            ServedModelName = "  claude-haiku  ",
            DockerImage = "  vllm/vllm-openai:latest  ",
            HostPort = 9000,
            DType = "bfloat16",
            GpuMemoryUtilization = 0.75,
            GpuVramGb = 24,
            MaxModelLength = 4096,
            ReadyTimeoutMinutes = 5,
            CpuOffloadGb = 2,
            WeightQuantization = "GPTQ",
            TensorParallelSize = 2,
            KCacheQuantization = "fp8",
            VCacheQuantization = "fp8",
            EnableTokenizersParallelism = false,
            AdditionalVllmArguments = "  --foo bar  ",
            MaxNumSeqs = 8,
            EnableVerboseLogs = true,
        };

        ServerProfileFormMapper.Apply(server, state);

        Assert.Multiple(() =>
        {
            Assert.That(server.Name, Is.EqualTo("OpenRouter"));
            Assert.That(server.UseExistingHttpServer, Is.True);
            Assert.That(server.HttpServerAddress, Is.EqualTo("https://openrouter.ai/api/v1"));
            Assert.That(server.Model, Is.EqualTo("anthropic/claude-haiku"));
            Assert.That(server.ServedModelName, Is.EqualTo("claude-haiku"));
            Assert.That(server.DockerImage, Is.EqualTo("vllm/vllm-openai:latest"));
            Assert.That(server.HostPort, Is.EqualTo(9000));
            Assert.That(server.DType, Is.EqualTo("bfloat16"));
            Assert.That(server.GpuMemoryUtilization, Is.EqualTo(0.75));
            Assert.That(server.GpuVramGb, Is.EqualTo(24));
            Assert.That(server.MaxModelLength, Is.EqualTo(4096));
            Assert.That(server.ReadyTimeoutMinutes, Is.EqualTo(5));
            Assert.That(server.CpuOffloadGb, Is.EqualTo(2));
            Assert.That(server.WeightQuantization, Is.EqualTo("GPTQ"));
            Assert.That(server.TensorParallelSize, Is.EqualTo(2));
            Assert.That(server.KvCacheDType, Is.EqualTo("fp8"));
            Assert.That(server.EnableTokenizersParallelism, Is.False);
            Assert.That(server.AdditionalVllmArguments, Is.EqualTo("--foo bar"));
            Assert.That(server.MaxNumSeqs, Is.EqualTo(8));
            Assert.That(server.EnableVerboseLogs, Is.True);
        });
    }

    [Test]
    public void Apply_PreservesServerValue_WhenFieldNotSupplied()
    {
        var server = new VllmServerConfiguration
        {
            HostPort = 7777,
            DType = "auto",
            MaxNumSeqs = 3,
            KvCacheDType = "fp16",
        };
        var state = new ServerProfileFormState { Name = "x" }; // all numeric/combo fields null

        ServerProfileFormMapper.Apply(server, state);

        Assert.Multiple(() =>
        {
            Assert.That(server.HostPort, Is.EqualTo(7777));
            Assert.That(server.DType, Is.EqualTo("auto"));
            Assert.That(server.MaxNumSeqs, Is.EqualTo(3));
        });
    }

    [Test]
    public void Apply_DoesNotTouchLocalModelPath()
    {
        var server = new VllmServerConfiguration { LocalModelPath = @"D:\models\qwen" };
        var state = new ServerProfileFormState { LocalModelPath = @"D:\other" };

        ServerProfileFormMapper.Apply(server, state);

        Assert.That(server.LocalModelPath, Is.EqualTo(@"D:\models\qwen"),
            "LocalModelPath is owned by the controller (metadata-rescan side effect) and must not be written by Apply.");
    }

    [Test]
    public void BuildThenApply_RoundTripsEditableFields()
    {
        var original = new VllmServerConfiguration
        {
            Name = "Profile",
            UseExistingHttpServer = true,
            HttpServerAddress = "http://localhost:8512",
            Model = "model-id",
            ServedModelName = "served",
            HostPort = 8512,
            DType = "half",
            GpuMemoryUtilization = 0.9,
            GpuVramGb = 0,
            MaxModelLength = 8192,
            WeightQuantization = "AWQ",
            TensorParallelSize = 1,
            KCacheQuantization = "fp8",
            VCacheQuantization = "fp8",
            EnableTokenizersParallelism = true,
            MaxNumSeqs = 5,
            EnableVerboseLogs = false,
        };

        var roundTripped = new VllmServerConfiguration();
        ServerProfileFormMapper.Apply(roundTripped, ServerProfileFormMapper.Build(original));

        Assert.Multiple(() =>
        {
            Assert.That(roundTripped.Name, Is.EqualTo(original.Name));
            Assert.That(roundTripped.UseExistingHttpServer, Is.EqualTo(original.UseExistingHttpServer));
            Assert.That(roundTripped.HttpServerAddress, Is.EqualTo(original.HttpServerAddress));
            Assert.That(roundTripped.Model, Is.EqualTo(original.Model));
            Assert.That(roundTripped.ServedModelName, Is.EqualTo(original.ServedModelName));
            Assert.That(roundTripped.HostPort, Is.EqualTo(original.HostPort));
            Assert.That(roundTripped.DType, Is.EqualTo(original.DType));
            Assert.That(roundTripped.GpuMemoryUtilization, Is.EqualTo(original.GpuMemoryUtilization));
            Assert.That(roundTripped.MaxModelLength, Is.EqualTo(original.MaxModelLength));
            Assert.That(roundTripped.WeightQuantization, Is.EqualTo(original.WeightQuantization));
            Assert.That(roundTripped.TensorParallelSize, Is.EqualTo(original.TensorParallelSize));
            Assert.That(roundTripped.KCacheQuantization, Is.EqualTo(original.KCacheQuantization));
            Assert.That(roundTripped.VCacheQuantization, Is.EqualTo(original.VCacheQuantization));
            Assert.That(roundTripped.EnableTokenizersParallelism, Is.EqualTo(original.EnableTokenizersParallelism));
            Assert.That(roundTripped.MaxNumSeqs, Is.EqualTo(original.MaxNumSeqs));
            Assert.That(roundTripped.EnableVerboseLogs, Is.EqualTo(original.EnableVerboseLogs));
        });
    }

    [TestCase("fp8", "fp8", "fallback", ExpectedResult = "fp8")]
    [TestCase("fp16", "fp16", "fallback", ExpectedResult = "fp16")]
    [TestCase("fp8", "fp16", "fallback", ExpectedResult = "fallback")]
    [TestCase("", "fp8", "fallback", ExpectedResult = "fallback")]
    [TestCase("fp8", "fp8", "", ExpectedResult = "fp8")]
    public string ResolveKvCacheDType_ResolvesMatchingQuantization(string? k, string? v, string fallback)
        => ServerProfileFormMapper.ResolveKvCacheDType(k, v, fallback);

    [Test]
    public void ParseExtraHeaders_AndFormatExtraHeaders_RoundTrip()
    {
        var text = "HTTP-Referer: https://app.example.com\r\nX-Title: My App";
        var parsed = ServerProfileFormMapper.ParseExtraHeaders(text);

        Assert.Multiple(() =>
        {
            Assert.That(parsed["HTTP-Referer"], Is.EqualTo("https://app.example.com"));
            Assert.That(parsed["X-Title"], Is.EqualTo("My App"));
            Assert.That(parsed.Count, Is.EqualTo(2));
        });

        Assert.That(ServerProfileFormMapper.FormatExtraHeaders(parsed).Replace("\r\n", "\n"),
            Is.EqualTo(text.Replace("\r\n", "\n")));
    }

    [Test]
    public void ParseExtraHeaders_IgnoresBlankAndMalformedLines()
    {
        var parsed = ServerProfileFormMapper.ParseExtraHeaders("good: 1\r\n\r\nno-colon\r\n : blankname");
        Assert.Multiple(() =>
        {
            Assert.That(parsed, Has.Count.EqualTo(1));
            Assert.That(parsed["good"], Is.EqualTo("1"));
        });
    }

    [Test]
    public void TryParseExtraBody_AcceptsObject_ReturnsNullError()
    {
        var error = ServerProfileFormMapper.TryParseExtraBody("{\"user\":\"abc\",\"n\":3}", out var extraBody);

        Assert.Multiple(() =>
        {
            Assert.That(error, Is.Null);
            Assert.That(extraBody, Is.Not.Null);
            Assert.That(extraBody!["user"].GetString(), Is.EqualTo("abc"));
            Assert.That(extraBody["n"].GetInt32(), Is.EqualTo(3));
        });
    }

    [Test]
    public void TryParseExtraBody_RejectsArrayAndInvalidJson()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ServerProfileFormMapper.TryParseExtraBody("[1,2,3]", out _), Is.Not.Null);
            Assert.That(ServerProfileFormMapper.TryParseExtraBody("{not json", out _), Is.Not.Null);
            Assert.That(ServerProfileFormMapper.TryParseExtraBody("   ", out _), Is.Null);
        });
    }

    [Test]
    public void FormatExtraBody_PrettyPrintsObject()
    {
        var error = ServerProfileFormMapper.TryParseExtraBody("{\"user\":\"abc\"}", out var extraBody);
        Assume.That(error, Is.Null);

        var formatted = ServerProfileFormMapper.FormatExtraBody(extraBody!);

        Assert.Multiple(() =>
        {
            Assert.That(formatted, Does.Contain("\"user\""));
            Assert.That(formatted, Does.Contain("abc"));
            Assert.That(formatted, Does.Contain(Environment.NewLine));
        });
    }

    [Test]
    public void FormatParameterCount_ReportsSourceFromMetadataWhenAvailable()
    {
        var fromMetadata = new VllmServerConfiguration { ParameterCountBillions = 7.5, Model = "x" };
        var derived = new VllmServerConfiguration { ParameterCountBillions = 0, Model = "Qwen3-7B" };

        Assert.Multiple(() =>
        {
            Assert.That(ServerProfileFormMapper.FormatParameterCount(fromMetadata), Does.Contain("model metadata"));
            Assert.That(ServerProfileFormMapper.FormatParameterCount(derived), Does.Contain("derived from model ID/name"));
        });
    }
}
