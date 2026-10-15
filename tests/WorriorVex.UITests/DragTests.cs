using Microsoft.Playwright;
using WorriorVex.Application.Notes;
using WorriorVex.Application.Tree;
using static Microsoft.Playwright.Assertions;

namespace WorriorVex.UITests;

[Collection("browser")]
public sealed class DragTests(BrowserFixture browser) : UITest(browser)
{
    [Fact]
    public async Task A_notebook_dragged_onto_another_becomes_a_folder_there_after_asking()
    {
        var notebooks = App.Get<INotebookService>();
        var source = await notebooks.CreateAsync("Databricks MI Certification");
        await notebooks.CreateAsync("Databricks");
        var chapter = await App.Get<ITreeService>().CreateFolderAsync(source.Id, null, "1.0 Designing");
        await App.Get<INoteService>().CreateAsync(source.Id, chapter.Id, "Introduction", "<p>x</p>");
        await Page.ReloadAsync();
        await Nav("Databricks MI Certification").WaitForAsync();

        await Page.Locator(".wn-navitem:text-is('Databricks MI Certification')")
            .DragToAsync(Page.Locator(".wn-navitem:text-is('Databricks')"));

        await Expect(Page.Locator(".wn-dialog h2")).ToContainTextAsync("Move the notebook");
        await Page.ClickAsync(".wn-dialog button:has-text('Move it there')");

        // Now a folder (level 1) under Databricks, holding its own folder and note.
        await Expect(Page.Locator(".wn-navitem:text-is('Databricks MI Certification')")).ToHaveAttributeAsync("data-level", "1");
        await Expect(Page.Locator(".wn-navitem:text-is('1.0 Designing')")).ToHaveAttributeAsync("data-level", "2");
        Assert.DoesNotContain(await notebooks.ListAsync(), n => n.Name == "Databricks MI Certification");
    }

    private async Task<string[]> FolderOrderAsync() =>
        [.. (await Page.Locator(".wn-navitem[data-level='1']").AllInnerTextsAsync()).Select(t => t.Trim())];

    [Fact]
    public async Task Folders_are_reordered_by_dropping_on_a_row_edge_or_with_move_up_and_down()
    {
        var notebook = await App.Get<INotebookService>().CreateAsync("Course");
        var tree = App.Get<ITreeService>();
        await tree.CreateFolderAsync(notebook.Id, null, "1.0 Designing");
        await tree.CreateFolderAsync(notebook.Id, null, "2.0 Data Preparation");
        await tree.CreateFolderAsync(notebook.Id, null, "3.0 Testing");
        await Page.ReloadAsync();
        await Nav("Course").WaitForAsync();
        Assert.Equal(["1.0 Designing", "2.0 Data Preparation", "3.0 Testing"], await FolderOrderAsync());

        // Onto the top edge of "1.0": placed before it, not inside it.
        var first = Page.Locator(".wn-navitem:text-is('1.0 Designing')");
        await Page.Locator(".wn-navitem:text-is('3.0 Testing')").DragToAsync(first, new LocatorDragToOptions { TargetPosition = new TargetPosition { X = 30, Y = 2 } });
        await Expect(Page.Locator(".wn-navitem[data-level='1']").First).ToHaveTextAsync("3.0 Testing");
        Assert.Equal(["3.0 Testing", "1.0 Designing", "2.0 Data Preparation"], await FolderOrderAsync());

        // Onto the middle of a row still puts it inside.
        var second = Page.Locator(".wn-navitem:text-is('2.0 Data Preparation')");
        var box = (await second.BoundingBoxAsync())!;
        await Page.Locator(".wn-navitem:text-is('1.0 Designing')").DragToAsync(second, new LocatorDragToOptions { TargetPosition = new TargetPosition { X = 30, Y = (float)box.Height / 2 } });
        await Expect(Page.Locator(".wn-navitem[data-level='1']")).ToHaveCountAsync(2);
        var folders = await tree.ListFoldersAsync(notebook.Id);
        Assert.Equal(folders.Single(f => f.Name == "2.0 Data Preparation").Id, folders.Single(f => f.Name == "1.0 Designing").ParentId);

        // Right-click → Move down.
        await Page.Locator(".wn-navitem:text-is('3.0 Testing')").ClickAsync(new LocatorClickOptions { Button = MouseButton.Right });
        await Page.ClickAsync(".wn-menu button:has-text('Move down')");
        await Expect(Page.Locator(".wn-navitem[data-level='1']").First).ToHaveTextAsync("2.0 Data Preparation");
    }

    [Fact]
    public async Task A_new_sub_notebook_goes_below_the_notes_and_items_move_among_each_other()
    {
        var notebook = await App.Get<INotebookService>().CreateAsync("Databricks");
        var notes = App.Get<INoteService>();
        await notes.CreateAsync(notebook.Id, null, "Note A");
        await notes.CreateAsync(notebook.Id, null, "Note B");
        await Page.ReloadAsync();
        await Nav("Databricks").HoverAsync();
        await Page.ClickAsync(".wn-navrow:has(.wn-navitem:text-is('Databricks')) .wn-row-add");
        await Page.ClickAsync(".wn-menu button:has-text('New folder')");
        await Page.FillAsync("#wn-prompt-input", "Sub");
        await Page.Keyboard.PressAsync("Enter");

        var rows = Page.Locator(".wn-navitem[data-level='1']");
        await Expect(rows).ToHaveTextAsync(["Note A", "Note B", "Sub"]);

        // Drag the folder onto the top half of "Note A": it goes above it.
        await Page.Locator(".wn-navitem:text-is('Sub')").DragToAsync(Page.Locator(".wn-navitem:text-is('Note A')"), new LocatorDragToOptions { TargetPosition = new TargetPosition { X = 30, Y = 3 } });
        await Expect(rows).ToHaveTextAsync(["Sub", "Note A", "Note B"]);

        // Alt+↓ on "Sub" takes it one place down.
        await Page.Locator(".wn-navitem:text-is('Sub')").FocusAsync();
        await Page.Keyboard.PressAsync("Alt+ArrowDown");
        await Expect(rows).ToHaveTextAsync(["Note A", "Sub", "Note B"]);
    }
}
