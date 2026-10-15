using Microsoft.EntityFrameworkCore;
using WorriorVex.Application.Common;
using WorriorVex.Application.Tree;
using WorriorVex.Domain;
using WorriorVex.Infrastructure.Notes;
using WorriorVex.Infrastructure.Persistence;

namespace WorriorVex.Infrastructure.Tree;

public sealed class TreeService(
    IDbContextFactory<WorriorVexDbContext> contextFactory,
    TimeProvider timeProvider) : ITreeService
{
    public async Task<IReadOnlyList<FolderSummary>> ListFoldersAsync(Guid? notebookId = null, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Nodes
            .AsNoTracking()
            .Where(n => n.Type == NodeType.Folder && n.DeletedAt == null && (notebookId == null || n.NotebookId == notebookId))
            .OrderBy(n => n.SortOrder)
            .ThenBy(n => n.Name)
            .Select(n => new FolderSummary(n.Id, n.NotebookId, n.ParentId, n.Name, n.SortOrder))
            .ToListAsync(cancellationToken);
    }

    public async Task<FolderSummary> CreateFolderAsync(Guid notebookId, Guid? parentId, string name, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureDestinationAsync(context, notebookId, parentId, cancellationToken);

        var sortOrder = await NoteService.NextSortOrderAsync(context, notebookId, parentId, cancellationToken);
        var folder = Node.CreateFolder(notebookId, parentId, name, timeProvider.GetUtcNow(), sortOrder);
        context.Nodes.Add(folder);
        await context.SaveChangesAsync(cancellationToken);
        return ToSummary(folder);
    }

    public async Task<FolderSummary> RenameFolderAsync(Guid folderId, string name, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var folder = await context.Nodes
            .FirstOrDefaultAsync(n => n.Id == folderId && n.Type == NodeType.Folder && n.DeletedAt == null, cancellationToken)
            ?? throw new EntityNotFoundException("Folder", folderId);

        folder.Rename(name, timeProvider.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken);
        return ToSummary(folder);
    }

    public async Task MoveAsync(Guid nodeId, Guid targetNotebookId, Guid? targetParentId, int? index = null, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var node = await context.Nodes.FirstOrDefaultAsync(n => n.Id == nodeId, cancellationToken)
            ?? throw new EntityNotFoundException("Item", nodeId);
        if (node.IsDeleted)
        {
            throw new DomainException("An item in the trash cannot be moved. Restore it first.");
        }

        await EnsureDestinationAsync(context, targetNotebookId, targetParentId, cancellationToken);

        var subtree = node.Type == NodeType.Folder
            ? await TreeQueries.GetSubtreeIdsAsync(context, node.NotebookId, node.Id, cancellationToken)
            : [node.Id];
        if (targetParentId is { } parentId && subtree.Contains(parentId))
        {
            throw new DomainException("A folder cannot be moved into itself or into one of its own folders.");
        }

        if (node.NotebookId != targetNotebookId && subtree.Count > 1)
        {
            // Everything inside travels to the other notebook as well, including what is in the trash.
            var descendants = await context.Nodes
                .Where(n => n.NotebookId == node.NotebookId && n.Id != node.Id)
                .ToListAsync(cancellationToken);
            foreach (var descendant in descendants.Where(d => subtree.Contains(d.Id)))
            {
                descendant.MoveTo(targetNotebookId, descendant.ParentId, descendant.SortOrder);
            }
        }

        if (index is { } position)
        {
            // An explicit position: renumber the siblings of the same kind around it.
            var siblings = await context.Nodes
                .Where(n => n.NotebookId == targetNotebookId
                    && n.ParentId == targetParentId
                    && n.Type == node.Type
                    && n.DeletedAt == null
                    && n.Id != node.Id)
                .OrderBy(n => n.SortOrder)
                .ThenBy(n => n.Name)
                .ToListAsync(cancellationToken);
            siblings.Insert(Math.Clamp(position, 0, siblings.Count), node);
            for (var i = 0; i < siblings.Count; i++)
            {
                if (siblings[i].Id == node.Id)
                {
                    node.MoveTo(targetNotebookId, targetParentId, i);
                }
                else
                {
                    siblings[i].SetSortOrder(i);
                }
            }
        }
        else if (node.NotebookId != targetNotebookId || node.ParentId != targetParentId)
        {
            var sortOrder = await NoteService.NextSortOrderAsync(context, targetNotebookId, targetParentId, cancellationToken);
            node.MoveTo(targetNotebookId, targetParentId, sortOrder);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A destination is a notebook outside the trash and, optionally, a folder outside the trash in it.</summary>
    public async Task MoveBesideAsync(Guid nodeId, Guid besideId, bool after, CancellationToken cancellationToken = default)
    {
        if (nodeId == besideId)
        {
            return;
        }

        BesidePlace place;
        await using (var lookup = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            place = await lookup.Nodes.Where(n => n.Id == besideId && n.DeletedAt == null)
                .Select(n => new BesidePlace(n.NotebookId, n.ParentId))
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new EntityNotFoundException("Item", besideId);
        }

        // Into the place first (this also checks a folder is not going into itself), then into position.
        await MoveAsync(nodeId, place.NotebookId, place.ParentId, cancellationToken: cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var siblings = await SiblingsAsync(context, place.NotebookId, place.ParentId, cancellationToken);
        var moving = siblings.First(n => n.Id == nodeId);
        siblings.Remove(moving);
        var index = siblings.FindIndex(n => n.Id == besideId);
        siblings.Insert(index < 0 ? siblings.Count : index + (after ? 1 : 0), moving);
        Renumber(siblings);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task MoveByAsync(Guid nodeId, int step, bool sameTypeOnly, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var node = await context.Nodes.FirstOrDefaultAsync(n => n.Id == nodeId && n.DeletedAt == null, cancellationToken)
            ?? throw new EntityNotFoundException("Item", nodeId);
        var siblings = await SiblingsAsync(context, node.NotebookId, node.ParentId, cancellationToken);
        var moving = siblings.First(n => n.Id == nodeId);
        var visible = sameTypeOnly ? siblings.Where(n => n.Type == moving.Type).ToList() : siblings;
        var target = visible.IndexOf(moving) + Math.Sign(step);
        if (step == 0 || target < 0 || target >= visible.Count)
        {
            return;
        }

        // Past the neighbour it steps over, whatever lies between them stays where it is.
        var neighbour = visible[target];
        siblings.Remove(moving);
        siblings.Insert(siblings.IndexOf(neighbour) + (step > 0 ? 1 : 0), moving);
        Renumber(siblings);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The notes and folders in one place, in their shared order.</summary>
    private static Task<List<Node>> SiblingsAsync(WorriorVexDbContext context, Guid notebookId, Guid? parentId, CancellationToken cancellationToken) =>
        context.Nodes
            .Where(n => n.NotebookId == notebookId && n.ParentId == parentId && n.DeletedAt == null)
            .OrderBy(n => n.SortOrder).ThenBy(n => n.CreatedAt)
            .ToListAsync(cancellationToken);

    private static void Renumber(List<Node> nodes)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            nodes[i].SetSortOrder(i);
        }
    }

    private sealed record BesidePlace(Guid NotebookId, Guid? ParentId);

    public async Task<FolderSummary> MoveNotebookIntoAsync(Guid notebookId, Guid targetNotebookId, Guid? targetParentId, CancellationToken cancellationToken = default)
    {
        if (notebookId == targetNotebookId)
        {
            throw new DomainException("A notebook cannot be moved into itself.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var notebook = await context.Notebooks.FirstOrDefaultAsync(n => n.Id == notebookId && n.DeletedAt == null, cancellationToken)
            ?? throw new EntityNotFoundException("Notebook", notebookId);
        if (notebook.Kind != NotebookKind.User)
        {
            throw new DomainException("The Inbox and the Calendar stay where they are.");
        }

        await EnsureDestinationAsync(context, targetNotebookId, targetParentId, cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var sortOrder = await NoteService.NextSortOrderAsync(context, targetNotebookId, targetParentId, cancellationToken);
        var folder = Node.CreateFolder(targetNotebookId, targetParentId, notebook.Name, timeProvider.GetUtcNow(), sortOrder);
        context.Nodes.Add(folder);

        // Everything keeps its place relative to the others; what sat at the top of the notebook goes under the new folder.
        var contents = await context.Nodes.Where(n => n.NotebookId == notebookId).ToListAsync(cancellationToken);
        foreach (var node in contents)
        {
            node.MoveTo(targetNotebookId, node.ParentId ?? folder.Id, node.SortOrder);
        }

        await context.SaveChangesAsync(cancellationToken);
        context.Notebooks.Remove(notebook);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToSummary(folder);
    }

    private static async Task EnsureDestinationAsync(WorriorVexDbContext context, Guid notebookId, Guid? parentId, CancellationToken cancellationToken)
    {
        if (!await context.Notebooks.AnyAsync(n => n.Id == notebookId && n.DeletedAt == null, cancellationToken))
        {
            throw new DomainException("That notebook no longer exists.");
        }

        if (parentId is { } folderId
            && !await context.Nodes.AnyAsync(
                n => n.Id == folderId && n.NotebookId == notebookId && n.Type == NodeType.Folder && n.DeletedAt == null,
                cancellationToken))
        {
            throw new DomainException("That folder no longer exists in the notebook.");
        }
    }

    private static FolderSummary ToSummary(Node folder) =>
        new(folder.Id, folder.NotebookId, folder.ParentId, folder.Name, folder.SortOrder);
}
