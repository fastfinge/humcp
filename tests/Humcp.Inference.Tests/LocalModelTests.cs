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
