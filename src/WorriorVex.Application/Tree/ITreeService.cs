namespace WorriorVex.Application.Tree;

/// <summary>A folder in a notebook tree.</summary>
public sealed record FolderSummary(Guid Id, Guid NotebookId, Guid? ParentId, string Name, int SortOrder);

/// <summary>Folders and the placement of folders and notes in the notebook trees.</summary>
public interface ITreeService
{
    /// <summary>Folders that are not in the trash, of one notebook or of all, in display order.</summary>
    Task<IReadOnlyList<FolderSummary>> ListFoldersAsync(Guid? notebookId = null, CancellationToken cancellationToken = default);

    /// <summary>Creates a folder after its siblings, at the top of the notebook or inside another folder.</summary>
    Task<FolderSummary> CreateFolderAsync(Guid notebookId, Guid? parentId, string name, CancellationToken cancellationToken = default);

    Task<FolderSummary> RenameFolderAsync(Guid folderId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a folder (with everything in it) or a note to another folder or notebook.
    /// <paramref name="index"/> is the position among the new siblings; omitted means last.
    /// </summary>
    Task MoveAsync(Guid nodeId, Guid targetNotebookId, Guid? targetParentId, int? index = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Places a note or folder right before or after another one, in that one's notebook and folder.
    /// Notes and folders share one order within a place, so either can sit anywhere among the others.
    /// </summary>
    Task MoveBesideAsync(Guid nodeId, Guid besideId, bool after, CancellationToken cancellationToken = default);

    /// <summary>
    /// One step up (negative) or down (positive) among the items in its place. With
    /// <paramref name="sameTypeOnly"/>, the step is to the next folder (or note) only, for views
    /// that show folders apart from notes.
    /// </summary>
    Task MoveByAsync(Guid nodeId, int step, bool sameTypeOnly, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a whole notebook inside another notebook or folder: it becomes a folder of the same name there,
    /// holding everything the notebook held (in the same arrangement, the trash included), and the
    /// notebook itself is gone. Only notebooks a person made can be moved like this.
    /// </summary>
    Task<FolderSummary> MoveNotebookIntoAsync(Guid notebookId, Guid targetNotebookId, Guid? targetParentId, CancellationToken cancellationToken = default);
}
