using Microsoft.EntityFrameworkCore;
using WorriorVex.Application.Content;
using WorriorVex.Application.Notes;
using WorriorVex.Application.Revisions;
using WorriorVex.Domain;
using WorriorVex.Infrastructure.Attachments;
using WorriorVex.Infrastructure.Links;
using WorriorVex.Infrastructure.Persistence;
using WorriorVex.Infrastructure.Search;

namespace WorriorVex.Infrastructure.Notes;

public sealed class NoteService(
    IDbContextFactory<WorriorVexDbContext> contextFactory,
    INoteHtmlSanitizer sanitizer,
    AttachmentFileStore files,
    TimeProvider timeProvider) : INoteService
{
    public async Task<NoteDetail> CreateAsync(
        Guid? notebookId = null,
        Guid? parentId = null,
        string? title = null,
        string? content = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        Guid targetNotebookId;
        if (parentId is { } folderId)
        {
            var folder = await context.Nodes
                .AsNoTracking()
                .FirstOrDefaultAsync(n => n.Id == folderId && n.Type == NodeType.Folder && n.DeletedAt == null, cancellationToken)
                ?? throw new DomainException("The folder for this note no longer exists.");
            if (notebookId is { } stated && stated != folder.NotebookId)
            {
                throw new DomainException("The folder is not in that notebook.");
            }

            targetNotebookId = folder.NotebookId;
        }
        else if (notebookId is { } requested)
        {
            if (!await context.Notebooks.AnyAsync(n => n.Id == requested && n.DeletedAt == null, cancellationToken))
            {
                throw new DomainException("The notebook for this note no longer exists.");
            }

            targetNotebookId = requested;
        }
        else
        {
            targetNotebookId = (await NotebookService.GetOrCreateInboxAsync(context, timeProvider, cancellationToken)).Id;
        }

        var sortOrder = await NextSortOrderAsync(context, targetNotebookId, parentId, cancellationToken);
        var node = Node.CreateNote(targetNotebookId, parentId, title, sanitizer.Sanitize(content), timeProvider.GetUtcNow(), sortOrder);
        context.Nodes.Add(node);

        // The note and its entry in the search index are written together or not at all.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await SearchIndex.IndexNoteAsync(context, node.Id, node.Name, node.Note!.Content, cancellationToken);
        await NoteLinkSync.SyncAsync(context, node.Id, node.Note.Content, node.CreatedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToDetail(node);
    }

    public async Task<NoteDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var node = await context.Nodes
            .AsNoTracking()
            .Include(n => n.Note)
            .FirstOrDefaultAsync(n => n.Id == id && n.Type == NodeType.Note, cancellationToken);
        return node is null ? null : ToDetail(node);
    }

    public async Task<IReadOnlyList<NoteSummary>> ListAsync(Guid notebookId, Guid? parentId = null, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await Summaries(context, LiveNotes(context)
            .Where(n => n.NotebookId == notebookId && n.ParentId == parentId)
            .OrderByDescending(n => n.IsPinned)
            .ThenByDescending(n => n.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NoteSummary>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await Summaries(context, LiveNotes(context).OrderByDescending(n => n.UpdatedAt)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NoteSummary>> ListFavoritesAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await Summaries(context, LiveNotes(context).Where(n => n.IsFavorite).OrderByDescending(n => n.UpdatedAt)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NoteSummary>> ListRecentAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await Summaries(context, LiveNotes(context)
                .Where(n => n.LastOpenedAt != null)
                .OrderByDescending(n => n.LastOpenedAt)
                .Take(Math.Clamp(limit, 1, 500)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NoteSummary>> ListByTagAsync(Guid tagId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await Summaries(context, LiveNotes(context)
                .Where(n => context.NoteTags.Any(nt => nt.TagId == tagId && nt.NoteId == n.Id))
                .OrderByDescending(n => n.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task RecordOpenedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var node = await context.Nodes.FirstOrDefaultAsync(n => n.Id == id && n.Type == NodeType.Note, cancellationToken)
            ?? throw new NoteNotFoundException(id);
        node.RecordOpened(timeProvider.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetFavoriteAsync(Guid id, bool isFavorite, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var node = await context.Nodes.FirstOrDefaultAsync(n => n.Id == id && n.Type == NodeType.Note, cancellationToken)
            ?? throw new NoteNotFoundException(id);
        node.SetFavorite(isFavorite);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetPinnedAsync(Guid id, bool isPinned, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var node = await context.Nodes.FirstOrDefaultAsync(n => n.Id == id && n.Type == NodeType.Note, cancellationToken)
            ?? throw new NoteNotFoundException(id);
        node.SetPinned(isPinned);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<NoteDetail> DuplicateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var original = await context.Nodes
            .AsNoTracking()
            .Include(n => n.Note)
            .FirstOrDefaultAsync(n => n.Id == id && n.Type == NodeType.Note, cancellationToken)
            ?? throw new NoteNotFoundException(id);
        if (original.IsDeleted)
        {
            throw new DomainException("A note in the trash cannot be duplicated. Restore it first.");
        }

        var now = timeProvider.GetUtcNow();
        var title = original.Name.Length + CopyPrefix.Length <= Node.MaxNameLength ? CopyPrefix + original.Name : original.Name;
        var sortOrder = await NextSortOrderAsync(context, original.NotebookId, original.ParentId, cancellationToken);
        var copy = Node.CreateNote(original.NotebookId, original.ParentId, title, original.Note!.Content, now, sortOrder);

        // The copy gets its own image files, so neither note depends on the other's attachments.
        var attachments = await context.Attachments.AsNoTracking().Where(a => a.NoteId == id).ToListAsync(cancellationToken);
        var content = copy.Note!.Content;
        var copiedFiles = new List<string>();
        foreach (var attachment in attachments)
        {
            var copied = files.Copy(attachment, copy.Id, now);
            if (copied is null)
            {
                continue;
            }

            copiedFiles.Add(copied.StoredFileName);
            context.Attachments.Add(copied);
            content = content.Replace(NoteContentRules.AttachmentSource(attachment.StoredFileName), NoteContentRules.AttachmentSource(copied.StoredFileName), StringComparison.Ordinal);
        }

        copy.Edit(title, content, now);
        context.Nodes.Add(copy);
        var tagIds = await context.NoteTags.Where(nt => nt.NoteId == id).Select(nt => nt.TagId).ToListAsync(cancellationToken);
        context.NoteTags.AddRange(tagIds.Select(tagId => NoteTag.Create(copy.Id, tagId)));

        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await SearchIndex.IndexNoteAsync(context, copy.Id, copy.Name, copy.Note.Content, cancellationToken);
            await NoteLinkSync.SyncAsync(context, copy.Id, copy.Note.Content, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            files.Delete(copiedFiles);
            throw;
        }

        return ToDetail(copy);
    }

    private const string CopyPrefix = "Copy of ";

    private static IQueryable<Node> LiveNotes(WorriorVexDbContext context) =>
        context.Nodes.AsNoTracking().Where(n => n.Type == NodeType.Note && n.DeletedAt == null);

    /// <summary>Rows for a list; the link count is the links to and from the note, found in the same query.</summary>
    private static IQueryable<NoteSummary> Summaries(WorriorVexDbContext context, IQueryable<Node> notes) =>
        notes.Select(n => new NoteSummary(n.Id, n.NotebookId, n.Name, n.CreatedAt, n.UpdatedAt, n.ParentId, n.IsFavorite, n.IsPinned, n.LastOpenedAt,
            context.NoteLinks.Count(l => l.SourceNoteId == n.Id || l.TargetNoteId == n.Id), n.SortOrder));

    public async Task<IReadOnlyList<NoteSummary>> ListLooseEndsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = LiveNotes(context)
            .Where(n => !context.NoteTags.Any(t => t.NoteId == n.Id)
                        && !context.NoteLinks.Any(l => l.SourceNoteId == n.Id || l.TargetNoteId == n.Id))
            .OrderByDescending(n => n.UpdatedAt);
        return await Summaries(context, query).ToListAsync(cancellationToken);
    }

    public async Task<NoteDetail> RenameAsync(Guid id, string title, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var node = await context.Nodes
            .Include(n => n.Note)
            .FirstOrDefaultAsync(n => n.Id == id && n.Type == NodeType.Note, cancellationToken)
            ?? throw new NoteNotFoundException(id);
        var previousTitle = node.Name;
        node.Rename(title, timeProvider.GetUtcNow());
        if (node.Name != previousTitle)
        {
            await context.SaveChangesAsync(cancellationToken);
            await SearchIndex.IndexNoteAsync(context, node.Id, node.Name, node.Note!.Content, cancellationToken);
        }

        return ToDetail(node);
    }

    public async Task<NoteDetail> UpdateAsync(Guid id, string? title, string? content, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var node = await context.Nodes
            .Include(n => n.Note)
            .FirstOrDefaultAsync(n => n.Id == id && n.Type == NodeType.Note, cancellationToken)
            ?? throw new NoteNotFoundException(id);

        var previousTitle = node.Name;
        var previousContent = node.Note!.Content;
        var now = timeProvider.GetUtcNow();
        node.Edit(title, sanitizer.Sanitize(content), now);

        var changed = node.Name != previousTitle || node.Note.Content != previousContent;
        if (changed && previousContent.Length > 0 && await RevisionIsDueAsync(context, node, now, cancellationToken))
        {
            context.NoteRevisions.Add(NoteRevision.Create(id, previousTitle, previousContent, now, RevisionPolicy.EditReason));
        }

        if (changed)
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await SearchIndex.IndexNoteAsync(context, node.Id, node.Name, node.Note.Content, cancellationToken);
            await NoteLinkSync.SyncAsync(context, node.Id, node.Note.Content, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return ToDetail(node);
    }

    /// <summary>The state before an edit is kept when nothing has been kept for the snapshot interval.</summary>
    private static async Task<bool> RevisionIsDueAsync(WorriorVexDbContext context, Node node, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var latest = await context.NoteRevisions
            .AsNoTracking()
            .Where(r => r.NoteId == node.Id)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => r.CreatedAt)
            .Take(1)
            .ToListAsync(cancellationToken);
        var lastKept = latest.Count > 0 ? latest[0] : node.CreatedAt;
        return now - lastKept >= RevisionPolicy.SnapshotInterval;
    }

    internal static async Task<int> NextSortOrderAsync(WorriorVexDbContext context, Guid notebookId, Guid? parentId, CancellationToken cancellationToken)
    {
        var highest = await context.Nodes
            .Where(n => n.NotebookId == notebookId && n.ParentId == parentId)
            .Select(n => (int?)n.SortOrder)
            .MaxAsync(cancellationToken);
        return highest is { } value ? value + 1 : 0;
    }

    internal static NoteDetail ToDetail(Node node) =>
        new(node.Id, node.NotebookId, node.Name, node.Note?.Content ?? string.Empty, node.CreatedAt, node.UpdatedAt, node.ParentId, node.DeletedAt, node.IsFavorite, node.IsPinned);
}
