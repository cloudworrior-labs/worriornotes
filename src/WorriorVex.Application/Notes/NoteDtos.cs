namespace WorriorVex.Application.Notes;

/// <summary>A row in a note list: enough to show and select a note, without its body.</summary>
public sealed record NoteSummary(
    Guid Id,
    Guid NotebookId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? ParentId = null,
    bool IsFavorite = false,
    bool IsPinned = false,
    DateTimeOffset? LastOpenedAt = null,
    int LinkCount = 0,
    int SortOrder = 0);

/// <summary>A note with its body, as opened in the editor.</summary>
public sealed record NoteDetail(
    Guid Id,
    Guid NotebookId,
    string Title,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? ParentId = null,
    DateTimeOffset? DeletedAt = null,
    bool IsFavorite = false,
    bool IsPinned = false)
{
    public NoteSummary ToSummary() => new(Id, NotebookId, Title, CreatedAt, UpdatedAt, ParentId, IsFavorite, IsPinned);
}

public sealed record NotebookSummary(Guid Id, string Name, bool IsInbox, bool IsCalendar = false);
