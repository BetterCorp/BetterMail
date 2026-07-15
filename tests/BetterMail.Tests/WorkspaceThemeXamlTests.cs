namespace BetterMail.Tests;

public sealed class WorkspaceThemeXamlTests
{
    [Fact]
    public void ThemeCalendarAndDriveUseSemanticResponsiveItemStyles()
    {
        var app = Read("App.axaml");
        var calendar = Read("CalendarWorkspaceView.axaml");
        var drive = Read("DriveWorkspaceView.axaml");
        var quote = (char)34;

        Assert.Equal(2, Count(app, "x:Key=" + quote + "BetterMailSearchBackgroundBrush" + quote));
        Assert.Contains("{DynamicResource BetterMailSearchBackgroundBrush}", app);
        Assert.Contains("{DynamicResource BetterMailSearchForegroundBrush}", app);
        Assert.Contains("ListBox.workspaceList ListBoxItem:selected", app);
        Assert.Contains("ListBox.workspaceList ListBoxItem:pointerover", app);
        Assert.Contains("ListBox.workspaceList ListBoxItem:focus", app);

        Assert.Equal(2, Count(calendar, "Classes=" + quote + "calendarEvent" + quote));
        Assert.Equal(0, Count(calendar, "BorderBrush=" + quote + "{Binding Color}" + quote));
        Assert.Equal(3, Count(calendar, "Background=" + quote + "{Binding Color}" + quote));
        Assert.Equal(2, Count(calendar, "Foreground=" + quote + "White" + quote));

        Assert.Equal(2, Count(drive, "Classes=" + quote + "workspaceList" + quote));
        Assert.Contains("Classes=" + quote + "workspaceTree" + quote, drive);
        Assert.Contains("ColumnDefinitions=" + quote + "3*,*,Auto" + quote, drive);
        Assert.Contains("ColumnDefinitions=" + quote + "3*,Auto" + quote, drive);
        Assert.DoesNotContain("ColumnDefinitions=" + quote + "*,120,90" + quote, drive);
    }

    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(Root(), "src", "BetterMail.App", name));

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BetterMail.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("BetterMail repository root was not found.");
    }
}
