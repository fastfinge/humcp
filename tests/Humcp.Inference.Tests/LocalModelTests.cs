using Humcp.Core;

namespace Humcp.Inference.Tests;

/// <summary>
/// The real model, on the real runtime (ROADMAP §11, Phase 9). Skipped where no model has been
/// downloaded — which is CI, and any machine that has not chosen to fetch 2.9 GB. Point
/// <c>HUMCP_MODELS</c> at a store holding <see cref="ModelCatalog.Cpu"/> to run them.
/// </summary>
public class LocalModelTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_cpu_model_loads_and_answers()
    {
        var store = ModelStore.Default();
        if (store.StateOf(ModelCatalog.Cpu) != ModelState.Ready)
            Assert.Skip($"No downloaded model in {store.FolderOf(ModelCatalog.Cpu)}.");

        using var model = await LocalModel.LoadAsync(store.FolderOf(ModelCatalog.Cpu), TestContext.Current.CancellationToken);
        var answer = await model.GenerateAsync("What is the capital of France? Answer in one word.", 10, null,
            TestContext.Current.CancellationToken);

        output.WriteLine($"Loaded in {model.LoadTime.TotalSeconds:F1} s; first word {answer.FirstToken.TotalSeconds:F1} s; " +
                         $"{answer.TokensPerSecond:F1} tokens a second; {answer.Text}");

        Assert.Contains("Paris", answer.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_cpu_model_describes_an_image_a_server_returned()
    {
        var store = ModelStore.Default();
        if (store.StateOf(ModelCatalog.Cpu) != ModelState.Ready)
            Assert.Skip($"No downloaded model in {store.FolderOf(ModelCatalog.Cpu)}.");

        var image = await File.ReadAllBytesAsync(Path.Combine(Fixtures(), "images", "weather-gc-ca.png"), TestContext.Current.CancellationToken);

        using var host = new ModelHost(store, ModelCatalog.Cpu);
        var model = await host.GetAsync();
        var description = await model.DescribeImageAsync(image, TestContext.Current.CancellationToken);

        output.WriteLine($"First word {description.FirstToken.TotalSeconds:F1} s, all {description.Total.TotalSeconds:F1} s, " +
                         $"{description.PromptTokens} tokens in: {description.Text}");

        // A Government of Canada forecast page. The words a description of it cannot do without.
        Assert.Contains("weather", description.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(description.Tokens < LocalModel.DescribeTokens, "The description ran to the cap instead of finishing.");
    }

    [Fact]
    public async Task A_host_with_no_model_says_how_to_get_one()
    {
        var empty = Directory.CreateTempSubdirectory("humcp-nomodel").FullName;
        try
        {
            using var host = new ModelHost(new ModelStore(empty), ModelCatalog.Cpu);

            Assert.False(host.IsDownloaded);
            var error = await Assert.ThrowsAsync<LocalModelException>(host.GetAsync);
            Assert.Contains("File, Local model", error.Message);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    private static string Fixtures()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Humcp.slnx"))) return Path.Combine(directory.FullName, "tests", "fixtures");

        throw new InvalidOperationException("Could not find Humcp.slnx above the test assembly.");
    }

    [Fact]
    public async Task A_folder_with_no_model_is_refused_in_a_sentence()
    {
        var empty = Directory.CreateTempSubdirectory("humcp-nomodel").FullName;
        try
        {
            var error = await Assert.ThrowsAsync<LocalModelException>(
                () => LocalModel.LoadAsync(empty, TestContext.Current.CancellationToken));

            Assert.StartsWith("The model could not be loaded", error.Message);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }
}
