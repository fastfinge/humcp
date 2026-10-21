using Humcp.Schema;

namespace Humcp.Core.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "humcp-settings-tests", Guid.NewGuid().ToString("n"), "settings.json");

    public void Dispose()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void The_defaults_hide_optional_fields_remember_history_and_speak_in_the_background()
    {
        var settings = AppSettings.Load(_path);

        Assert.False(settings.ShowOptionalFields);
        Assert.True(settings.RememberHistory);
        Assert.True(settings.SpeakInBackground);
        Assert.True(settings.DescribeImages);
        Assert.Null(settings.TreePaneWidth);
    }

    [Fact]
    public void Describing_images_can_be_turned_off_and_stays_off()
    {
        var settings = AppSettings.Load(_path);
        settings.DescribeImages = false;
        Assert.True(settings.Save());

        Assert.False(AppSettings.Load(_path).DescribeImages);
    }

    [Fact]
    public void Round_trips_through_the_file()
    {
        var settings = AppSettings.Load(_path);
        settings.ShowOptionalFields = true;
        settings.RememberHistory = false;
        settings.SpeakInBackground = false;
        settings.TreePaneWidth = 512.4;

        Assert.True(settings.Save());

        var read = AppSettings.Load(_path);
        Assert.True(read.ShowOptionalFields);
        Assert.False(read.RememberHistory);
        Assert.False(read.SpeakInBackground);
        Assert.Equal(512, read.TreePaneWidth);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_the_defaults_and_says_why()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json");

        var settings = AppSettings.Load(_path);

        Assert.True(settings.RememberHistory);
        Assert.NotNull(settings.LastError);
    }

    [Fact]
    public void The_form_is_check_boxes_and_reads_back_what_it_was_filled_with()
    {
        Assert.All(AppSettings.Model.Fields, f => Assert.Equal(FieldKind.Boolean, f.Kind));

        var settings = AppSettings.InMemory();
        settings.RememberHistory = false;

        var state = FormState.Create(AppSettings.Model);
        settings.Fill(state);

        ((BooleanFieldState)state.Find(AppSettings.SpeakInBackgroundField)!).Value = false;
        settings.Read(state);

        Assert.False(settings.RememberHistory);
        Assert.False(settings.SpeakInBackground);
    }
}
