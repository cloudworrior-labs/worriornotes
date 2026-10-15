using WorriorVex.Application.Common;
using WorriorVex.Application.Trash;
using WorriorVex.Domain;

namespace WorriorVex.IntegrationTests;

public class TreeAndTrashTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Notebooks_are_created_in_order_renamed_and_reordered()
    {
        using var data = new TempDataDirectory();
        await using var app = await TestApp.StartAsync(data.Path);

        var a = await app.Notebooks.CreateAsync("Development");
        var b = await app.Notebooks.CreateAsync("Personal");
        var c = await app.Notebooks.CreateAsync("Reference");
        await app.Notebooks.RenameAsync(b.Id, "Private");
        await app.Notebooks.ReorderAsync(c.Id, 0);

        var listed = await app.Notebooks.ListAsync();

        Assert.Equal(["Inbox", "Reference", "Development", "Private"], listed.Select(n => n.Name));
        Assert.True(listed[0].IsInbox);
        _ = a;
    }

    [Fact]
    public async Task Folders_nest_and_notes_are_listed_in_the_folder_they_were_created_in()
    {
        using var data = new TempDataDirectory();
        await using var app = await TestApp.StartAsync(data.Path);
        var notebook = await app.Notebooks.CreateAsync("Projects");

        var outer = await app.Tree.CreateFolderAsync(notebook.Id, null, "WorriorVex");
        var inner = await app.Tree.CreateFolderAsync(notebook.Id, outer.Id, "Design");
        var second = await app.Tree.CreateFolderAsync(notebook.Id, null, "Archive");
        var atTop = await app.Notes.CreateAsync(notebook.Id, title: "Top");
        var inInner = await app.Notes.CreateAsync(parentId: inner.Id, title: "Colours");

        var folders = await app.Tree.ListFoldersAsync(notebook.Id);

        Assert.Equal(["Archive", "Design", "WorriorVex"], folders.Select(f => f.Name).Order());
        Assert.Equal(outer.Id, folders.Single(f => f.Id == inner.Id).ParentId);
        Assert.True(folders.Single(f => f.Id == outer.Id).SortOrder < folders.Single(f => f.Id == second.Id).SortOrder);
        Assert.Equal(notebook.Id, inInner.NotebookId);
        Assert.Equal(atTop.Id, Assert.Single(await app.Notes.ListAsync(notebook.Id)).Id);
        Assert.Equal(inInner.Id, Assert.Single(await app.Notes.ListAsync(notebook.Id, inner.Id)).Id);
        Assert.Empty(await app.Notes.ListAsync(notebook.Id, outer.Id));
        Assert.Equal(2, (await app.Notes.ListAllAsync()).Count);
    }

    [Fact]
    public async Task A_folder_or_note_cannot_be_created_in_something_that_is_not_a_live_folder()
    {
        using var data = new TempDataDirectory();
        await using var app = await TestApp.StartAsync(data.Path);
        var notebook = await app.Notebooks.CreateAsync("Projects");
        var other = await app.Notebooks.CreateAsync("Other");
        var note = await app.Notes.CreateAsync(notebook.Id, title: "A note");
        var folder = await app.Tree.CreateFolderAsync(notebook.Id, null, "Folder");
        await app.Trash.MoveToTrashAsync(folder.Id);

        await Assert.ThrowsAsync<DomainException>(() => app.Tree.CreateFolderAsync(notebook.Id, note.Id, "Under a note"));
        await Assert.ThrowsAsync<DomainException>(() => app.Notes.CreateAsync(parentId: note.Id));
        await Assert.ThrowsAsync<DomainException>(() => app.Notes.CreateAsync(parentId: folder.Id));
        await Assert.ThrowsAsync<DomainException>(() => app.Tree.CreateFolderAsync(other.Id, folder.Id, "Wrong notebook"));
        await Assert.ThrowsAsync<DomainException>(() => app.Tree.CreateFolderAsync(Guid.NewGuid(), null, "Nowhere"));
    }

    [Fact]
    public async Task Moving_a_folder_to_another_notebook_takes_everything_inside_along()
    {
        using var data = new TempDataDirectory();
        await using var app = await TestApp.StartAsync(data.Path);
        var from = await app.Notebooks.CreateAsync("From");
        var to = await app.Notebooks.CreateAsync("To");
        var folder = await app.Tree.CreateFolderAsync(from.Id, null, "Folder");
        var sub = await app.Tree.CreateFolderAsync(from.Id, folder.Id, "Sub");
        var note = await app.Notes.CreateAsync(parentId: sub.Id, title: "Deep note");

        await app.Tree.MoveAsync(folder.Id, to.Id, null);

        Assert.Empty(await app.Tree.ListFoldersAsync(from.Id));
        Assert.Equal(["Folder", "Sub"], (await app.Tree.ListFoldersAsync(to.Id)).Select(f => f.Name).Order());
        Assert.Equal(to.Id, (await app.Notes.GetAsync(note.Id))!.NotebookId);
        Assert.Equal(note.Id, Assert.Single(await app.Notes.ListAsync(to.Id, sub.Id)).Id);
    }

    [Fact]
    public async Task A_folder_cannot_be_moved_into_itself_or_below_itself()
    {
        using var data = new TempDataDirectory();
        await using var app = await TestApp.StartAsync(data.Path);
        var notebook = await app.Notebooks.CreateAsync("Projects");
        var folder = await app.Tree.CreateFolderAsync(notebook.Id, null, "Folder");
        var sub = await app.Tree.CreateFolderAsync(notebook.Id, folder.Id, "Sub");

        await Assert.ThrowsAsync<DomainException>(() => app.Tree.MoveAsync(folder.Id, notebook.Id, folder.Id));
        await Assert.ThrowsAsync<DomainException>(() => app.Tree.MoveAsync(folder.Id, notebook.Id, sub.Id));
        Assert.Null((await app.Tree.ListFoldersAsync(notebook.Id)).Single(f => f.Id == folder.Id).ParentId);
    }

    [Fact]
    public async Task Moving_with_a_position_reorders_the_folders_beside_it()
    {
        using var data = new TempDataDirectory();
        await using var app = await TestApp.StartAsync(data.Path);
        var notebook = await app.Notebooks.CreateAsync("Projects");
        var a = await app.Tree.CreateFolderAsync(notebook.Id, null, "A");
        var b = await app.Tree.CreateFolderAsync(notebook.Id, null, "B");
        var c = await app.Tree.CreateFolderAsync(notebook.Id, null, "C");

        await app.Tree.MoveAsync(c.Id, notebook.Id, null, index: 0);
        await app.Tree.RenameFolderAsync(a.Id, "Alpha");

        Assert.Equal(["C", "Alpha", "B"], (await app.Tree.ListFoldersAsync(notebook.Id)).Select(f => f.Name));
        _ = b;
    }

    [Fact]
    public async Task A_note_moved_to_another_folder_is_listed_there_with_UpdatedAt_untouched()
    {
        using var data = new TempDataDirectory();
        var clock = new ManualTimeProvider(Start);
        await using var app = await TestApp.StartAsync(data, clock);
        var notebook = await app.Notebooks.CreateAsync("Projects");
        var folder = await app.Tree.CreateFolderAsync(notebook.Id, null, "Folder");
        var note = await app.Notes.CreateAsync(title: "Captured in the Inbox");

        clock.Advance(TimeSpan.FromHours(1));
        await app.Tree.MoveAsync(note.Id, notebook.Id, folder.Id);

        var moved = Assert.Single(await app.Notes.ListAsync(notebook.Id, folder.Id));
        Assert.Equal(note.Id, moved.Id);
        Assert.Equal(Start, moved.UpdatedAt);
        Assert.Empty(await app.Notes.ListAsync((await app.Notebooks.GetInboxAsync()).Id));
    }

    [Fact]
    public async Task A_deleted_note_leaves_the_lists_shows_in_the_trash_and_comes_back_intact()
    {
        using var data = new TempDataDirectory();
        var clock = new ManualTimeProvider(Start);
        await using var app = await TestApp.StartAsync(data, clock);
        var note = await app.Notes.CreateAsync(title: "Keep me", content: "<p>important</p>");

        clock.Advance(TimeSpan.FromMinutes(5));
        await app.Trash.MoveToTrashAsync(note.Id);

        Assert.Empty(await app.Notes.ListAllAsync());
        var item = Assert.Single(await app.Trash.ListAsync());
        Assert.Equal((note.Id, TrashItemKind.Note, "Keep me", "Inbox"), (item.Id, item.Kind, item.Name, item.OriginalLocation));
        Assert.Equal(Start.AddMinutes(5), item.DeletedAt);
        Assert.NotNull((await app.Notes.GetAsync(note.Id))!.DeletedAt);
        await Assert.ThrowsAsync<DomainException>(() => app.Notes.UpdateAsync(note.Id, "Changed", "<p>x</p>"));

        var result = await app.Trash.RestoreAsync(note.Id);

        Assert.False(result.Relocated);
        Assert.Empty(await app.Trash.ListAsync());
        var restored = await app.Notes.GetAsync(note.Id);
        Assert.Null(restored!.DeletedAt);
        Assert.Equal("<p>important</p>", restored.Content);
        Assert.Equal(Start, restored.UpdatedAt);
    }

    [Fact]
    public async Task A_deleted_folder_is_one_trash_item_and_restores_with_what_was_in_it()
    {
        using var data = new TempDataDirectory();
        var clock = new ManualTimeProvider(Start);
        await using var app = await TestApp.StartAsync(data, clock);
        var notebook = await app.Notebooks.CreateAsync("Projects");
        var folder = await app.Tree.CreateFolderAsync(notebook.Id, null, "Folder");
        var sub = await app.Tree.CreateFolderAsync(notebook.Id, folder.Id, "Sub");
        var early = await app.Notes.CreateAsync(parentId: folder.Id, title: "Deleted earlier");
        var inside = await app.Notes.CreateAsync(parentId: sub.Id, title: "Inside");

        clock.Advance(TimeSpan.FromMinutes(1));
        await app.Trash.MoveToTrashAsync(early.Id);
        clock.Advance(TimeSpan.FromMinutes(1));
        await app.Trash.MoveToTrashAsync(folder.Id);

        Assert.Empty(await app.Tree.ListFoldersAsync(notebook.Id));
        Assert.Empty(await app.Notes.ListAllAsync());
        Assert.Equal(["Folder", "Deleted earlier"], (await app.Trash.ListAsync()).Select(i => i.Name));

        await app.Trash.RestoreAsync(folder.Id);

        Assert.Equal(2, (await app.Tree.ListFoldersAsync(notebook.Id)).Count);
        Assert.Equal(inside.Id, Assert.Single(await app.Notes.ListAllAsync()).Id);
        Assert.Equal("Deleted earlier", Assert.Single(await app.Trash.ListAsync()).Name);
    }

    [Fact]
    public async Task Restoring_into_a_place_that_is_gone_puts_the_item_where_it_can_be_found()
    {
        using var data = new TempDataDirectory();
        var clock = new ManualTimeProvider(Start);
        await using var app = await TestApp.StartAsync(data, clock);
        var notebook = await app.Notebooks.CreateAsync("Projects");
        var folder = await app.Tree.CreateFolderAsync(notebook.Id, null, "Folder");
        var inFolder = await app.Notes.CreateAsync(parentId: folder.Id, title: "In folder");
        var inNotebook = await app.Notes.CreateAsync(notebook.Id, title: "In notebook");

        await app.Trash.MoveToTrashAsync(inFolder.Id);
        clock.Advance(TimeSpan.FromMinutes(1));
        await app.Trash.MoveToTrashAsync(folder.Id);
        var toNotebookTop = await app.Trash.RestoreAsync(inFolder.Id);

        Assert.True(toNotebookTop.Relocated);
        Assert.Equal((notebook.Id, (Guid?)null), (toNotebookTop.NotebookId, toNotebookTop.ParentId));

        clock.Advance(TimeSpan.FromMinutes(1));
        await app.Trash.MoveToTrashAsync(inNotebook.Id);
        clock.Advance(TimeSpan.FromMinutes(1));
        await app.Trash.MoveNotebookToTrashAsync(notebook.Id);
        var toInbox = await app.Trash.RestoreAsync(inNotebook.Id);

        var inbox = await app.Notebooks.GetInboxAsync();
        Assert.True(toInbox.Relocated);
        Assert.Equal(inbox.Id, toInbox.NotebookId);
        Assert.Equal(inNotebook.Id, Assert.Single(await app.Notes.ListAsync(inbox.Id)).Id);
    }

    [Fact]
    public async Task A_deleted_notebook_disappears_and_restores_whole_and_the_inbox_cannot_be_deleted()
    {
        using var data = new TempDataDirectory();
        var clock = new ManualTimeProvider(Start);
        await using var app = await TestApp.StartAsync(data, clock);
        var notebook = await app.Notebooks.CreateAsync("Projects");
        var folder = await app.Tree.CreateFolderAsync(notebook.Id, null, "Folder");
        await app.Notes.CreateAsync(parentId: folder.Id, title: "Note");
        var inbox = await app.Notebooks.GetInboxAsync();

        clock.Advance(TimeSpan.FromMinutes(1));
        await app.Trash.MoveNotebookToTrashAsync(notebook.Id);

        Assert.Equal(["Inbox"], (await app.Notebooks.ListAsync()).Select(n => n.Name));
        Assert.Empty(await app.Notes.ListAllAsync());
        var item = Assert.Single(await app.Trash.ListAsync());
        Assert.Equal((TrashItemKind.Notebook, "Projects"), (item.Kind, item.Name));
        await Assert.ThrowsAsync<DomainException>(() => app.Notes.CreateAsync(notebook.Id));
        await Assert.ThrowsAsync<DomainException>(() => app.Trash.MoveNotebookToTrashAsync(inbox.Id));

        await app.Trash.RestoreAsync(notebook.Id);

        Assert.Equal(2, (await app.Notebooks.ListAsync()).Count);
        Assert.Single(await app.Tree.ListFoldersAsync(notebook.Id));
        Assert.Single(await app.Notes.ListAllAsync());
        Assert.Empty(await app.Trash.ListAsync());
    }

    [Fact]
    public async Task Only_what_is_in_the_trash_can_be_deleted_permanently_and_then_it_is_gone()
    {
        using var data = new TempDataDirectory();
        await using var app = await TestApp.StartAsync(data.Path);
        var notebook = await app.Notebooks.CreateAsync("Projects");
        var folder = await app.Tree.CreateFolderAsync(notebook.Id, null, "Folder");
        var note = await app.Notes.CreateAsync(parentId: folder.Id, title: "Note");
        var kept = await app.Notes.CreateAsync(notebook.Id, title: "Kept");

        await Assert.ThrowsAsync<DomainException>(() => app.Trash.DeletePermanentlyAsync(folder.Id));
        await Assert.ThrowsAsync<DomainException>(() => app.Trash.DeletePermanentlyAsync(notebook.Id));

        await app.Trash.MoveToTrashAsync(folder.Id);
        await app.Trash.DeletePermanentlyAsync(folder.Id);

        Assert.Empty(await app.Trash.ListAsync());
        Assert.Null(await app.Notes.GetAsync(note.Id));
        Assert.NotNull(await app.Notes.GetAsync(kept.Id));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => app.Trash.RestoreAsync(folder.Id));
    }

    [Fact]
    public async Task Emptying_the_trash_removes_everything_in_it_and_nothing_else()
    {
        using var data = new TempDataDirectory();
        var clock = new ManualTimeProvider(Start);
        await using var app = await TestApp.StartAsync(data, clock);
        var doomed = await app.Notebooks.CreateAsync("Doomed");
        await app.Notes.CreateAsync(doomed.Id, title: "In doomed notebook");
        var loose = await app.Notes.CreateAsync(title: "Loose");
        var kept = await app.Notes.CreateAsync(title: "Kept");

        await app.Trash.MoveToTrashAsync(loose.Id);
        clock.Advance(TimeSpan.FromMinutes(1));
        await app.Trash.MoveNotebookToTrashAsync(doomed.Id);

        Assert.Equal(2, await app.Trash.EmptyAsync());

        Assert.Empty(await app.Trash.ListAsync());
        Assert.Equal(kept.Id, Assert.Single(await app.Notes.ListAllAsync()).Id);
        Assert.Equal(["Inbox"], (await app.Notebooks.ListAsync()).Select(n => n.Name));
        Assert.Equal(0, await app.Trash.EmptyAsync());
    }

    [Fact]
    public async Task A_notebook_moved_into_another_becomes_a_folder_with_everything_in_place()
    {
        using var data = new TempDataDirectory();
        await using var app = await TestApp.StartAsync(data.Path);
        var source = await app.Notebooks.CreateAsync("Databricks MI Certification");
        var target = await app.Notebooks.CreateAsync("Databricks");
        var chapter = await app.Tree.CreateFolderAsync(source.Id, null, "1.0 Designing");
        var deep = await app.Tree.CreateFolderAsync(source.Id, chapter.Id, "prompts");
        var top = await app.Notes.CreateAsync(source.Id, null, "2.0 Data Preparation", "<p>overview</p>");
        var inner = await app.Notes.CreateAsync(source.Id, deep.Id, "Structured output", "<p>json schema</p>");
        var binned = await app.Notes.CreateAsync(source.Id, chapter.Id, "Old draft", "<p>x</p>");
        await app.Trash.MoveToTrashAsync(binned.Id);

        var folder = await app.Tree.MoveNotebookIntoAsync(source.Id, target.Id, null);

        Assert.Equal(("Databricks MI Certification", target.Id, (Guid?)null), (folder.Name, folder.NotebookId, folder.ParentId));
        Assert.DoesNotContain(await app.Notebooks.ListAsync(), n => n.Id == source.Id);
        var folders = await app.Tree.ListFoldersAsync(target.Id);
        Assert.Equal(folder.Id, folders.Single(f => f.Id == chapter.Id).ParentId);
        Assert.Equal(chapter.Id, folders.Single(f => f.Id == deep.Id).ParentId);
        var topNote = (await app.Notes.GetAsync(top.Id))!;
        Assert.Equal((target.Id, (Guid?)folder.Id), (topNote.NotebookId, topNote.ParentId));
        Assert.Equal((target.Id, (Guid?)deep.Id), ((await app.Notes.GetAsync(inner.Id))!.NotebookId, (await app.Notes.GetAsync(inner.Id))!.ParentId));
        Assert.Equal("<p>json schema</p>", (await app.Notes.GetAsync(inner.Id))!.Content);

        // The trashed note moved too, and restores into the moved tree.
        await app.Trash.RestoreAsync(binned.Id);
        Assert.Equal((target.Id, (Guid?)chapter.Id), ((await app.Notes.GetAsync(binned.Id))!.NotebookId, (await app.Notes.GetAsync(binned.Id))!.ParentId));

        // Search still finds it, now in the new place.
        var hit = Assert.Single(await app.Search.SearchAsync("schema"));
        Assert.StartsWith("Databricks / Databricks MI Certification", hit.Location);
    }

    [Fact]
    public async Task The_inbox_and_a_notebook_into_itself_are_refused()
    {
        using var data = new TempDataDirectory();
        await using var app = await TestApp.StartAsync(data.Path);
        var inbox = await app.Notebooks.GetInboxAsync();
        var notebook = await app.Notebooks.CreateAsync("Projects");

        await Assert.ThrowsAsync<WorriorVex.Domain.DomainException>(() => app.Tree.MoveNotebookIntoAsync(inbox.Id, notebook.Id, null));
        await Assert.ThrowsAsync<WorriorVex.Domain.DomainException>(() => app.Tree.MoveNotebookIntoAsync(notebook.Id, notebook.Id, null));
    }

    [Fact]
    public async Task Notes_and_folders_share_one_order_that_can_be_changed()
    {
        using var data = new TempDataDirectory();
        await using var app = await TestApp.StartAsync(data.Path);
        var notebook = await app.Notebooks.CreateAsync("Databricks");
        var a = await app.Notes.CreateAsync(notebook.Id, null, "Note A");
        var b = await app.Notes.CreateAsync(notebook.Id, null, "Note B");
        var folder = await app.Tree.CreateFolderAsync(notebook.Id, null, "New sub-notebook");

        async Task<string[]> Order()
        {
            var notes = await app.Notes.ListAsync(notebook.Id);
            var folders = await app.Tree.ListFoldersAsync(notebook.Id);
            return [.. notes.Select(n => (n.SortOrder, n.Title)).Concat(folders.Select(f => (f.SortOrder, f.Name))).OrderBy(x => x.SortOrder).Select(x => x.Item2)];
        }

        // Created last, so last: below the notes already there.
        Assert.Equal(["Note A", "Note B", "New sub-notebook"], await Order());

        await app.Tree.MoveBesideAsync(folder.Id, a.Id, after: false);
        Assert.Equal(["New sub-notebook", "Note A", "Note B"], await Order());

        await app.Tree.MoveByAsync(b.Id, -1, sameTypeOnly: false);
        Assert.Equal(["New sub-notebook", "Note B", "Note A"], await Order());

        // A note moved beside an item in another folder goes into that folder.
        var inside = await app.Notes.CreateAsync(notebook.Id, folder.Id, "Inside");
        await app.Tree.MoveBesideAsync(a.Id, inside.Id, after: true);
        Assert.Equal(folder.Id, (await app.Notes.GetAsync(a.Id))!.ParentId);
        Assert.Equal(["Inside", "Note A"], (await app.Notes.ListAsync(notebook.Id, folder.Id)).OrderBy(n => n.SortOrder).Select(n => n.Title));

        // A folder cannot go beside something inside itself.
        await Assert.ThrowsAsync<WorriorVex.Domain.DomainException>(() => app.Tree.MoveBesideAsync(folder.Id, inside.Id, after: true));
    }
}
