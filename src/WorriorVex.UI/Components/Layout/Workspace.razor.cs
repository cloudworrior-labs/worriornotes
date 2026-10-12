using WorriorVex.UI.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using WorriorVex.Application.Export;
using WorriorVex.Application.Notes;
using WorriorVex.Application.Platform;
using WorriorVex.Application.Search;
using WorriorVex.Application.Settings;
using WorriorVex.Application.Tags;
using WorriorVex.Application.Trash;
using WorriorVex.Application.Tree;
using WorriorVex.Application.Updates;
using WorriorVex.Domain;
using Microsoft.AspNetCore.Components.Web;
using WorriorVex.UI.Components.Dialogs;
using WorriorVex.UI.Components.Navigation;
using WorriorVex.UI.Components.Notes;
using WorriorVex.UI.Components.Search;

namespace WorriorVex.UI.Components.Layout;

/// <summary>
/// The three-pane workspace. It holds what is selected and asks the application services to do
/// the work; rules about notes, folders and the trash live in those services, not here.
/// </summary>
public partial class Workspace
{
    private readonly string _modifier = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl";

    private List<NotebookSummary> _notebooks = [];
    private List<FolderSummary> _folders = [];
    private List<NoteSummary> _notes = [];
    private List<NoteSummary> _treeNotes = [];
    private bool _templatePicker;
    private string? _pendingPackage;
    [Inject] private StartupStatus Startup { get; set; } = default!;
    private NavPane? _navPane;
    private IJSObjectReference? _treeKeysHandle;
    private List<TrashItem> _trash = [];
    private List<TagSummary> _tags = [];
    private MenuState? _menu;
    private MoveRequest? _move;
    private NoteSummary? _export;
    private (Guid Id, bool IsFolder)? _dragging;
    private Guid? _draggingNotebook;
    private int _connectionsVersion;
    private bool _sidebarHidden;
    private bool _focusMode;
    private bool _confirmedDeletion;
    private UpdateCheck? _update;
    private NavSelection _selection = new NavSelection.AllNotes();
    private NoteDetail? _current;
    private TrashItem? _trashItem;
    private NoteEditor? _editor;

    private SearchBox? _searchBox;
    private List<NoteSearchResult> _searchResults = [];
    private string _searchText = string.Empty;
    private NavSelection? _beforeSearch;
    private bool _searchScoped;
    private string? _suggestion;
    private int _searchMark = -1;
    private int _searchRun;
    private bool _searching;

    private PromptRequest? _prompt;
    private ConfirmRequest? _confirm;
    private string? _dialogError;
    private string? _error;
    private string? _notice;
    private bool _loading = true;
    private bool _focusNewNote;

    private const string NavWidthVariable = "--wn-nav-width";
    private const string ListWidthVariable = "--wn-list-width";
    private ElementReference _navResizer;
    private ElementReference _listResizer;
    private IJSObjectReference? _navResizerHandle;
    private IJSObjectReference? _listResizerHandle;
    private readonly SemaphoreSlim _attachGate = new(1, 1);
    private bool _disposed;
    private string? _navResizerAttachedTo;
    private string? _listResizerAttachedTo;
    private string? _treeKeysAttachedTo;

    private IJSObjectReference? _shortcutsModule;
    private IJSObjectReference? _shortcuts;
    private DotNetObjectReference<Workspace>? _self;

    [Inject] private INoteService Notes { get; set; } = default!;
    [Inject] private INotebookService Notebooks { get; set; } = default!;
    [Inject] private ITreeService Tree { get; set; } = default!;
    [Inject] private ITrashService Trash { get; set; } = default!;
    [Inject] private INoteSearchService Search { get; set; } = default!;
    [Inject] private ITagService TagService { get; set; } = default!;
    [Inject] private IExportService Exporter { get; set; } = default!;
    [Inject] private ISettingsService Settings { get; set; } = default!;
    [Inject] private Translator T { get; set; } = default!;
    [Inject] private WorriorVex.Application.Templates.ITemplateService TemplateService { get; set; } = default!;
    [Inject] private IUpdateChecker Updates { get; set; } = default!;
    [Inject] private WorriorVex.Application.Backup.IBackupScheduler BackupScheduler { get; set; } = default!;
    [Inject] private IPlatformShell Shell { get; set; } = default!;
    [Inject] private NoteAutosaver Autosaver { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<Workspace> Logger { get; set; } = default!;

    private NotebookSummary? Inbox => _notebooks.FirstOrDefault(n => n.IsInbox);

    private Dictionary<Guid, string> NotebookNames => _notebooks.ToDictionary(n => n.Id, n => n.Name);

    private string Heading => _selection switch
    {
        NavSelection.Notebook notebook when _notebooks.Any(n => n.Id == notebook.NotebookId && n.IsCalendar) => T["All calendar notes"],
        NavSelection.Notebook notebook => _notebooks.FirstOrDefault(n => n.Id == notebook.NotebookId)?.Name ?? T["Notebook"],
        NavSelection.Folder folder => _folders.FirstOrDefault(f => f.Id == folder.FolderId)?.Name ?? T["Folder"],
        NavSelection.Trash => T["Trash"],
        NavSelection.Favorites => T["Favorites"],
        NavSelection.Recent => T["Recent"],
        NavSelection.LooseEnds => T["Loose ends"],
        NavSelection.Tag tag => _tags.FirstOrDefault(t => t.Id == tag.TagId)?.Name ?? T["Tag"],
        _ => T["All Notes"],
    };

    /// <summary>The notebook or folder a search can be limited to: the one that was open when the search began.</summary>
    private string? SearchScopeName => _beforeSearch switch
    {
        NavSelection.Notebook notebook => _notebooks.FirstOrDefault(n => n.Id == notebook.NotebookId)?.Name,
        NavSelection.Folder folder => _folders.FirstOrDefault(f => f.Id == folder.FolderId)?.Name,
        _ => null,
    };

    private SearchScope? CurrentSearchScope => _searchScoped ? _beforeSearch switch
    {
        NavSelection.Notebook notebook => new SearchScope(notebook.NotebookId),
        NavSelection.Folder folder => new SearchScope(folder.NotebookId, folder.FolderId),
        _ => null,
    } : null;

    /// <summary>Lists that gather notes from several notebooks say which notebook each note is in.</summary>
    private bool ShowsManyPlaces => _selection is NavSelection.AllNotes or NavSelection.Favorites or NavSelection.Recent or NavSelection.Tag or NavSelection.LooseEnds;

    private string? EmptyMessage => _selection switch
    {
        NavSelection.Favorites => T["No favorites yet. Mark a note with ★ to find it here."],
        NavSelection.Recent => T["Notes you open appear here, most recent first."],
        NavSelection.LooseEnds => T["Every note has a tag or a link. Nothing to tidy."],
        NavSelection.Tag => T["No notes carry this tag."],
        _ => null,
    };

    /// <summary>Pages that use the width of the list and the editor together and show no note of their own.</summary>
    private bool IsHelpPage => _selection is NavSelection.Documentation or NavSelection.About or NavSelection.Data or NavSelection.Settings or NavSelection.Calendar;

    /// <summary>Nothing has been written yet: the Inbox is the only notebook and it is empty.</summary>
    private bool _firstRun;

    /// <summary>Folders can be created in a user's notebook or in a folder, not in the Inbox or a combined list.</summary>
    private bool CanHoldFolders => _selection switch
    {
        NavSelection.Notebook notebook => notebook.NotebookId != Inbox?.Id && !_notebooks.Any(n => n.Id == notebook.NotebookId && n.IsCalendar),
        NavSelection.Folder => true,
        _ => false,
    };

    /// <summary>What is selected can be renamed and deleted: a user's notebook or a folder.</summary>
    private bool CanChangeContainer => CanHoldFolders;

    protected override async Task OnInitializedAsync()
    {
        Autosaver.StateChanged += OnSaveStateChanged;
        Autosaver.NoteSaved += OnNoteSaved;
        Settings.Changed += OnSettingsChanged;
        T.Changed += OnLanguageChanged;
        Autosaver.Delay = TimeSpan.FromMilliseconds(Settings.Current.AutosaveDelayMilliseconds);

        try
        {
            await LoadStructureAsync();
            _selection = StartingPlace();
            await LoadListAsync(openFirst: true);
            _firstRun = _notebooks.Count == 1 && _notes.Count == 0 && (await Trash.ListAsync()).Count == 0;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Loading notes failed");
            _error = T["Your notes could not be loaded. Nothing has been changed on disk."];
        }
        finally
        {
            _loading = false;
        }

        _checkingForUpdates = Settings.Current.CheckForUpdates;
        _ = KeepCheckingForUpdatesAsync();

        BackupScheduler.Completed += OnAutoBackup;
        BackupScheduler.Start();

        if (Startup.PackageToImport is { } package)
        {
            Startup.PackageToImport = null;
            _pendingPackage = package;
            _selection = new NavSelection.Data();
            _notice = T["Opening the package “{0}”…", Path.GetFileName(package)];
        }
    }

    /// <summary>Runs in the background after start; a newer version shows as a banner, anything else is silent.</summary>
    private void OnLanguageChanged() => _ = InvokeAsync(StateHasChanged);

    private void OnAutoBackup(string? path, Exception? error) => _ = InvokeAsync(() =>
    {
        if (error is null)
        {
            _notice = T["Automatic backup saved to {0}.", Path.GetFileName(path)];
        }
        else
        {
            _error = T["The automatic backup failed. Details are in the log file."];
        }

        StateHasChanged();
    });

    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);
    private readonly CancellationTokenSource _stopping = new();
    private bool _checkingForUpdates;

    /// <summary>At start and every few hours while the app is open, when the setting allows. Switching the setting on checks at once.</summary>
    private async Task KeepCheckingForUpdatesAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                if (Settings.Current.CheckForUpdates)
                {
                    await CheckForUpdatesAsync();
                }

                await Task.Delay(UpdateCheckInterval, _stopping.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var check = await Updates.CheckAsync(_stopping.Token);
            if (check is { IsNewer: true })
            {
                await InvokeAsync(() =>
                {
                    _update = check;
                    StateHasChanged();
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogInformation(ex, "The update check did not complete");
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _self = DotNetObjectReference.Create(this);
            _shortcutsModule = await JS.InvokeAsync<IJSObjectReference>("import", UiAssets.Shortcuts);
            _shortcuts = await _shortcutsModule.InvokeAsync<IJSObjectReference>("register", _self);
            await ApplyAppearanceAsync(Settings.Current);
            await _shortcutsModule.InvokeVoidAsync("setPaneWidth", NavWidthVariable, Settings.Current.NavigationWidth);
            await _shortcutsModule.InvokeVoidAsync("setPaneWidth", ListWidthVariable, Settings.Current.ListWidth);
        }

        if (_shortcutsModule is null || _disposed)
        {
            return;
        }

        // The pane handles and the tree get their JavaScript attached to the element that is in the page now.
        // Elements are created afresh whenever the workspace re-keys (a language change) or a pane comes back
        // after a help page, so each is attached again whenever its element changes. Renders overlap, so only
        // one pass runs at a time; a render that arrives meanwhile is followed by another pass of its own.
        if (await _attachGate.WaitAsync(0))
        {
            try
            {
                (_navResizerHandle, _navResizerAttachedTo) = await AttachAsync(_navResizerHandle, _navResizerAttachedTo, _navResizer,
                    () => _shortcutsModule.InvokeAsync<IJSObjectReference>("attachResizer", _navResizer, ".wn-nav", NavWidthVariable, AppSettings.MinPaneWidth, AppSettings.MaxPaneWidth, _self));
                (_listResizerHandle, _listResizerAttachedTo) = await AttachAsync(_listResizerHandle, _listResizerAttachedTo, IsHelpPage ? default : _listResizer,
                    () => _shortcutsModule.InvokeAsync<IJSObjectReference>("attachResizer", _listResizer, ".wn-notelist", ListWidthVariable, AppSettings.MinPaneWidth, AppSettings.MaxPaneWidth, _self));
                (_treeKeysHandle, _treeKeysAttachedTo) = await AttachAsync(_treeKeysHandle, _treeKeysAttachedTo, _navPane?.Root ?? default,
                    () => _shortcutsModule.InvokeAsync<IJSObjectReference>("attachTreeKeys", _navPane!.Root));
            }
            catch (JSException ex)
            {
                Logger.LogWarning(ex, "A pane handle could not be attached");
            }
            finally
            {
                _attachGate.Release();
            }
        }

        if (_focusNewNote && _editor is not null)
        {
            _focusNewNote = false;
            _editor.FocusTitleOnNextLoad();
            StateHasChanged();
        }
    }

    /// <summary>
    /// Keeps a JavaScript handle attached to the current element: detaches when the element went away or
    /// was replaced (its reference id changed) and attaches to the new one. A default reference means "none".
    /// </summary>
    private static async Task<(IJSObjectReference? Handle, string? AttachedTo)> AttachAsync(IJSObjectReference? handle, string? attachedTo, ElementReference element, Func<ValueTask<IJSObjectReference>> attach)
    {
        var id = string.IsNullOrEmpty(element.Id) ? null : element.Id;
        if (id == attachedTo)
        {
            return (handle, attachedTo);
        }

        if (handle is not null)
        {
            try
            {
                await handle.InvokeVoidAsync("dispose");
                await handle.DisposeAsync();
            }
            catch (Exception ex) when (ex is JSException or ObjectDisposedException or JSDisconnectedException)
            {
                // The old element is gone with its listeners; nothing left to detach.
            }
        }

        return (id is null ? null : await attach(), id);
    }

    /// <summary>A pane edge was dragged; remember the width.</summary>
    [JSInvokable]
    public async Task OnPaneResized(string variable, int width)
    {
        var settings = variable == NavWidthVariable
            ? Settings.Current with { NavigationWidth = width }
            : Settings.Current with { ListWidth = width };
        try
        {
            await Settings.SaveAsync(settings);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The pane width could not be remembered");
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task ResizeByKeyAsync(KeyboardEventArgs args, string variable)
    {
        var step = args.Key switch { "ArrowLeft" => -16, "ArrowRight" => 16, _ => 0 };
        if (step == 0 || _shortcutsModule is null)
        {
            return;
        }

        var current = variable == NavWidthVariable ? Settings.Current.NavigationWidth : Settings.Current.ListWidth;
        var width = Math.Clamp(current + step, AppSettings.MinPaneWidth, AppSettings.MaxPaneWidth);
        await _shortcutsModule.InvokeVoidAsync("setPaneWidth", variable, width);
        await OnPaneResized(variable, width);
    }

    [JSInvokable]
    public Task OnShortcut(string action)
    {
        if (_prompt is not null || _confirm is not null || _move is not null || _menu is not null)
        {
            return Task.CompletedTask;
        }

        return action switch
        {
            "newNote" => InvokeAsync(NewNoteAsync),
            "search" => InvokeAsync(async () =>
            {
                if (_searchBox is not null)
                {
                    await _searchBox.FocusAsync();
                }
            }),
            "toggleSidebar" => InvokeAsync(() => _sidebarHidden = !_sidebarHidden),
            "focusMode" => InvokeAsync(() => _focusMode = !_focusMode),
            "save" => InvokeAsync(SaveNowAsync),
            "newFromTemplate" => InvokeAsync(() => _templatePicker = true),
            "find" => InvokeAsync(async () =>
            {
                if (_editor is not null && _current is not null && !IsHelpPage)
                {
                    await _editor.OpenFindAsync();
                }
            }),
            _ => Task.CompletedTask,
        };
    }

    // ---- Loading -------------------------------------------------------------------------------

    private async Task LoadStructureAsync()
    {
        _notebooks = [.. await Notebooks.ListAsync()];
        _folders = [.. await Tree.ListFoldersAsync()];
        await LoadTreeNotesAsync();
        await LoadTagsAsync();
    }

    /// <summary>The notes shown as leaves of the navigation tree, when that is switched on.</summary>
    private async Task LoadTreeNotesAsync()
    {
        if (!Settings.Current.ShowNotesInTree)
        {
            _treeNotes = [];
            return;
        }

        try
        {
            _treeNotes = [.. Sorted(await Notes.ListAllAsync())];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The notes for the tree could not be loaded");
        }
    }

    private string NotebookName(Guid id) => _notebooks.FirstOrDefault(n => n.Id == id)?.Name ?? "notebook";
    private string FolderName(Guid id) => _folders.FirstOrDefault(f => f.Id == id)?.Name ?? "folder";

    private Task RenameNotebookAsync((Guid NotebookId, string Name) rename) => RunAsync(T["The notebook could not be renamed."], async () =>
    {
        await Notebooks.RenameAsync(rename.NotebookId, rename.Name);
        await LoadStructureAsync();
    });

    private Task RenameFolderAsync((Guid FolderId, string Name) rename) => RunAsync(T["The folder could not be renamed."], async () =>
    {
        await Tree.RenameFolderAsync(rename.FolderId, rename.Name);
        await LoadStructureAsync();
    });

    private Task RenameNoteAsync((Guid NoteId, string Title) rename) => RunAsync(T["The note could not be renamed."], async () =>
    {
        if (_current?.Id == rename.NoteId)
        {
            await Autosaver.FlushAsync();
        }

        var saved = await Notes.RenameAsync(rename.NoteId, rename.Title);
        if (_current?.Id == rename.NoteId)
        {
            _current = saved;
            _editor?.FocusTitleOnNextLoad();
        }

        ReplaceSummary(saved.ToSummary());
        await LoadTreeNotesAsync();
    });

    private async Task SaveTreeStateAsync((IReadOnlyList<Guid> ExpandedFolders, IReadOnlyList<Guid> CollapsedNotebooks) state)
    {
        try
        {
            await Settings.SaveAsync(Settings.Current with { ExpandedFolders = state.ExpandedFolders, CollapsedNotebooks = state.CollapsedNotebooks });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The tree state could not be remembered");
        }
    }

    private async Task SetSortAsync(NoteSort sort)
    {
        await Settings.SaveAsync(Settings.Current with { NoteSort = sort });
        SortNotes();
        _treeNotes = [.. Sorted(_treeNotes)];
    }

    /// <summary>Pinned first, then by the chosen order.</summary>
    private IEnumerable<NoteSummary> Sorted(IEnumerable<NoteSummary> notes) => Settings.Current.NoteSort switch
    {
        NoteSort.Added => notes.OrderByDescending(n => n.IsPinned).ThenBy(n => n.CreatedAt),
        NoteSort.Created => notes.OrderByDescending(n => n.IsPinned).ThenByDescending(n => n.CreatedAt),
        NoteSort.Title => notes.OrderByDescending(n => n.IsPinned).ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase),
        _ => notes.OrderByDescending(n => n.IsPinned).ThenByDescending(n => n.UpdatedAt),
    };

    private string? StatusDetail
    {
        get
        {
            if (_current is null || IsHelpPage)
            {
                return null;
            }

            var words = TextStats.CountWords(_current.Content);
            var minutes = TextStats.ReadingMinutes(words);
            var saved = _current.UpdatedAt.ToLocalTime().ToString("t");
            return minutes == 0 ? T["{0} words · saved {1}", words, saved] : T["{0} words · {1} min read · saved {2}", words, minutes, saved];
        }
    }

    private async Task LoadTagsAsync()
    {
        try
        {
            _tags = [.. await TagService.ListAsync()];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Tags could not be loaded");
        }
    }

    private async Task LoadListAsync(bool openFirst)
    {
        _trashItem = null;
        switch (_selection)
        {
            case NavSelection.Trash:
                _trash = [.. await Trash.ListAsync()];
                _notes = [];
                _current = null;
                return;
            case NavSelection.Documentation or NavSelection.About or NavSelection.Data or NavSelection.Settings or NavSelection.Search:
                _notes = [];
                _current = null;
                return;
            case NavSelection.Notebook notebook:
                _notes = [.. await Notes.ListAsync(notebook.NotebookId)];
                break;
            case NavSelection.Folder folder:
                _notes = [.. await Notes.ListAsync(folder.NotebookId, folder.FolderId)];
                break;
            case NavSelection.Favorites:
                _notes = [.. await Notes.ListFavoritesAsync()];
                break;
            case NavSelection.Recent:
                _notes = [.. await Notes.ListRecentAsync()];
                break;
            case NavSelection.LooseEnds:
                _notes = [.. await Notes.ListLooseEndsAsync()];
                break;
            case NavSelection.Tag tag:
                _notes = [.. await Notes.ListByTagAsync(tag.TagId)];
                break;
            default:
                _notes = [.. await Notes.ListAllAsync()];
                break;
        }

        if (openFirst)
        {
            _current = _notes.Count > 0 ? await Notes.GetAsync(_notes[0].Id) : null;
        }
    }

    // ---- Navigation and notes ------------------------------------------------------------------

    private async Task SelectAsync(NavSelection selection)
    {
        if (selection is NavSelection.SavedSearch saved)
        {
            await RunSavedSearchAsync(saved.Index);
            return;
        }

        if (selection == _selection || !await SaveBeforeLeavingAsync())
        {
            return;
        }

        await RunAsync(T["That could not be opened."], async () =>
        {
            EndSearch();
            _selection = selection;
            _notice = null;
            await LoadListAsync(openFirst: true);
            await RememberPlaceAsync();
        });
    }

    // ---- Settings ------------------------------------------------------------------------------

    /// <summary>Where to open: the Inbox, or where the user was last if that is wanted and still exists.</summary>
    private NavSelection StartingPlace()
    {
        var settings = Settings.Current;
        if (settings.OpenLastPlace && settings.LastNotebookId is { } notebookId && _notebooks.Any(n => n.Id == notebookId))
        {
            if (settings.LastFolderId is { } folderId && _folders.Any(f => f.Id == folderId && f.NotebookId == notebookId))
            {
                return new NavSelection.Folder(notebookId, folderId);
            }

            return new NavSelection.Notebook(notebookId);
        }

        return Inbox is { } inbox ? new NavSelection.Notebook(inbox.Id) : new NavSelection.AllNotes();
    }

    private async Task RememberPlaceAsync()
    {
        var (notebookId, folderId) = _selection switch
        {
            NavSelection.Folder folder => (folder.NotebookId, (Guid?)folder.FolderId),
            NavSelection.Notebook notebook => (notebook.NotebookId, null),
            _ => (Guid.Empty, null),
        };
        if (notebookId == Guid.Empty || (Settings.Current.LastNotebookId == notebookId && Settings.Current.LastFolderId == folderId))
        {
            return;
        }

        try
        {
            await Settings.SaveAsync(Settings.Current with { LastNotebookId = notebookId, LastFolderId = folderId });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The last place could not be remembered");
        }
    }

    private void OnSettingsChanged(AppSettings settings) => _ = InvokeAsync(async () =>
    {
        Autosaver.Delay = TimeSpan.FromMilliseconds(settings.AutosaveDelayMilliseconds);
        await ApplyAppearanceAsync(settings);
        if (settings.CheckForUpdates && !_checkingForUpdates)
        {
            _ = CheckForUpdatesAsync();
        }

        _checkingForUpdates = settings.CheckForUpdates;
        if (settings.ShowNotesInTree != (_treeNotes.Count > 0 || !settings.ShowNotesInTree))
        {
            await LoadTreeNotesAsync();
        }

        StateHasChanged();
    });

    private async Task ApplyAppearanceAsync(AppSettings settings)
    {
        if (_shortcutsModule is null)
        {
            return;
        }

        try
        {
            await _shortcutsModule.InvokeVoidAsync("applyAppearance", settings.Theme.ToString().ToLowerInvariant(), settings.EditorFontSize, settings.EditorLineHeight,
                settings.Accent.ToString().ToLowerInvariant(), settings.EditorFont.ToString().ToLowerInvariant());
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "The appearance settings could not be applied");
        }
    }

    private async Task NewNoteAsync()
    {
        if (!await SaveBeforeLeavingAsync())
        {
            return;
        }

        await RunAsync(T["The note could not be created."], async () =>
        {
            NoteDetail note;
            switch (_selection)
            {
                case NavSelection.Folder folder:
                    note = await Notes.CreateAsync(folder.NotebookId, folder.FolderId);
                    break;
                case NavSelection.Notebook notebook:
                    note = await Notes.CreateAsync(notebook.NotebookId);
                    break;
                case NavSelection.Trash or NavSelection.Documentation or NavSelection.About or NavSelection.Data or NavSelection.Settings or NavSelection.Search or NavSelection.Calendar:
                    // Nothing is created in the trash or on a help page: a new note goes to the Inbox, and so does the view.
                    note = await Notes.CreateAsync();
                    EndSearch();
                    _selection = new NavSelection.Notebook(note.NotebookId);
                    await LoadListAsync(openFirst: false);
                    break;
                case NavSelection.Tag tag:
                    // A note made while looking at a tag gets that tag, so it stays in view.
                    note = await Notes.CreateAsync();
                    if (_tags.FirstOrDefault(t => t.Id == tag.TagId) is { } tagged)
                    {
                        await TagService.AddToNoteAsync(note.Id, tagged.Name);
                        await LoadTagsAsync();
                    }

                    break;
                case NavSelection.Favorites:
                    note = await Notes.CreateAsync();
                    await Notes.SetFavoriteAsync(note.Id, true);
                    note = note with { IsFavorite = true };
                    break;
                default:
                    note = await Notes.CreateAsync();
                    break;
            }

            _notes.RemoveAll(n => n.Id == note.Id);
            _notes.Add(note.ToSummary());
            SortNotes();
            _current = note;
            _focusNewNote = true;
            _firstRun = false;
            _notice = null;
            await LoadTreeNotesAsync();
            await RecordOpenedAsync(note.Id);
        });
    }

    /// <summary>A template was picked: a note made from it goes where a plain new note would go.</summary>
    private async Task NewFromTemplateAsync(Guid templateId)
    {
        _templatePicker = false;
        if (!await SaveBeforeLeavingAsync())
        {
            return;
        }

        await RunAsync(T["The note could not be created."], async () =>
        {
            (Guid? notebookId, Guid? parentId) = _selection switch
            {
                NavSelection.Folder folder => (folder.NotebookId, folder.FolderId),
                NavSelection.Notebook notebook => (notebook.NotebookId, (Guid?)null),
                _ => ((Guid?)null, (Guid?)null),
            };
            var note = await TemplateService.CreateFromAsync(templateId, notebookId, parentId);
            if (_selection is not (NavSelection.Folder or NavSelection.Notebook))
            {
                EndSearch();
                _selection = new NavSelection.Notebook(note.NotebookId);
                await LoadListAsync(openFirst: false);
            }

            _notes.RemoveAll(n => n.Id == note.Id);
            _notes.Add(note.ToSummary());
            SortNotes();
            _current = note;
            _focusNewNote = true;
            _firstRun = false;
            _notice = null;
            await LoadStructureAsync();
            await RecordOpenedAsync(note.Id);
        });
    }

    private async Task OpenAsync(Guid id)
    {
        if (id == _current?.Id || !await SaveBeforeLeavingAsync())
        {
            return;
        }

        await RunAsync(T["The note could not be opened."], async () =>
        {
            var note = await Notes.GetAsync(id);
            if (note is null || note.DeletedAt is not null)
            {
                _notes.RemoveAll(n => n.Id == id);
                _error = T["That note is no longer here."];
                return;
            }

            _current = note;
            await RecordOpenedAsync(note.Id);
        });
    }

    /// <summary>A link inside a note was followed. The note opens wherever it is filed.</summary>
    private async Task OpenLinkedNoteAsync(Guid id)
    {
        if (id == _current?.Id)
        {
            return;
        }

        var note = await Notes.GetAsync(id);
        if (note is null)
        {
            _error = T["The note this link points to no longer exists."];
            return;
        }

        if (note.DeletedAt is not null)
        {
            _error = T["“{0}” is in the trash. Restore it to open it.", note.Title];
            return;
        }

        if (_notes.All(n => n.Id != id) && _selection is not NavSelection.Search)
        {
            // Show the note in its own place, so the list beside it makes sense.
            var place = note.ParentId is { } folder ? new NavSelection.Folder(note.NotebookId, folder) : (NavSelection)new NavSelection.Notebook(note.NotebookId);
            if (!await SaveBeforeLeavingAsync())
            {
                return;
            }

            await RunAsync(T["The note could not be opened."], async () =>
            {
                EndSearch();
                _selection = place;
                _notice = null;
                await LoadListAsync(openFirst: false);
            });
        }

        await OpenAsync(id);
    }

    /// <summary>An earlier version of the open note was restored; show it and keep the list in step.</summary>
    private async Task OnRestoredAsync(NoteDetail restored)
    {
        await Autosaver.FlushAsync();
        _current = restored;
        ReplaceSummary(restored.ToSummary());
        SortNotes();
        _connectionsVersion++;
        _notice = T["“{0}” was restored to an earlier version. The version it replaced is kept in its history.", restored.Title];
    }

    private async Task OnTagsChangedAsync()
    {
        _connectionsVersion++;
        await LoadTagsAsync();
    }

    /// <summary>Remembers the note for the Recent list. Not worth an error if it fails.</summary>
    private async Task RecordOpenedAsync(Guid id)
    {
        try
        {
            await Notes.RecordOpenedAsync(id);
            var index = _notes.FindIndex(n => n.Id == id);
            if (index >= 0)
            {
                _notes[index] = _notes[index] with { LastOpenedAt = DateTimeOffset.UtcNow };
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The note could not be marked as opened");
        }
    }

    private async Task ToggleFavoriteAsync()
    {
        if (_current is not { } note)
        {
            return;
        }

        await RunAsync(T["The favorite could not be changed."], async () =>
        {
            var favorite = !note.IsFavorite;
            await Notes.SetFavoriteAsync(note.Id, favorite);
            _current = note with { IsFavorite = favorite };
            ReplaceSummary(_current.ToSummary());
            if (_selection is NavSelection.Favorites && !favorite)
            {
                _notes.RemoveAll(n => n.Id == note.Id);
            }
        });
    }

    private async Task TogglePinnedAsync()
    {
        if (_current is not { } note)
        {
            return;
        }

        await RunAsync(T["The pin could not be changed."], async () =>
        {
            var pinned = !note.IsPinned;
            await Notes.SetPinnedAsync(note.Id, pinned);
            _current = note with { IsPinned = pinned };
            ReplaceSummary(_current.ToSummary());
            SortNotes();
        });
    }

    private void ReplaceSummary(NoteSummary summary)
    {
        var index = _notes.FindIndex(n => n.Id == summary.Id);
        if (index >= 0)
        {
            _notes[index] = summary with { LastOpenedAt = _notes[index].LastOpenedAt };
        }
    }

    private void SortNotes()
    {
        _notes = _selection is NavSelection.Notebook or NavSelection.Folder ? [.. Sorted(_notes)] : _notes;
    }

    private async Task DeleteCurrentNoteAsync()
    {
        if (_current is not { } note || !await SaveBeforeLeavingAsync())
        {
            return;
        }

        if (Settings.Current.ConfirmDeletion && !_confirmedDeletion)
        {
            OpenConfirm(new ConfirmRequest(
                T["Move “{0}” to the trash?", note.Title], T["You can restore it from the Trash later."], T["Move to trash"], Danger: false,
                async () =>
                {
                    _confirmedDeletion = true;
                    try
                    {
                        await DeleteCurrentNoteAsync();
                    }
                    finally
                    {
                        _confirmedDeletion = false;
                    }
                }));
            return;
        }

        await RunAsync(T["The note could not be moved to the trash."], async () =>
        {
            await Trash.MoveToTrashAsync(note.Id);
            _searchResults.RemoveAll(r => r.NoteId == note.Id);
            var index = _notes.FindIndex(n => n.Id == note.Id);
            _notes.RemoveAll(n => n.Id == note.Id);
            var next = _notes.Count == 0 ? null : _notes[Math.Clamp(index, 0, _notes.Count - 1)];
            _current = next is null ? null : await Notes.GetAsync(next.Id);
            _treeNotes.RemoveAll(n => n.Id == note.Id);
            _notice = T["“{0}” was moved to the trash.", note.Title];
        });
    }

    private void OnEdited(NoteEdit edit) => Autosaver.Edit(edit.NoteId, edit.Title, edit.Content);

    private async Task SaveNowAsync()
    {
        // A help page or the calendar shows no editor; the reference to the last one is stale then.
        if (_editor is not null && !IsHelpPage)
        {
            await _editor.FlushAsync();
        }

        await Autosaver.FlushAsync();
    }

    /// <summary>Leaving a note with edits that cannot be saved would lose them, so stay put and say why.</summary>
    private async Task<bool> SaveBeforeLeavingAsync()
    {
        if (_editor is not null && _current is not null && !IsHelpPage)
        {
            await _editor.FlushAsync();
        }

        if (await Autosaver.FlushAsync())
        {
            return true;
        }

        _error = T["This note could not be saved, so it stays open. Retry the save before doing anything else."];
        return false;
    }

    // ---- Search --------------------------------------------------------------------------------

    /// <summary>Runs when the text in the search box has settled. The list pane becomes the list of results.</summary>
    private async Task SearchAsync(string text)
    {
        if (_selection is not NavSelection.Search)
        {
            if (string.IsNullOrWhiteSpace(text) || !await SaveBeforeLeavingAsync())
            {
                return;
            }

            _beforeSearch = _selection;
            _selection = new NavSelection.Search();
            _notes = [];
            _current = null;
            _trashItem = null;
            _notice = null;
        }

        if (text == _searchText && _searchResults.Count > 0)
        {
            // Enter re-sends the text that was already searched for: keep the results and the mark on them.
            return;
        }

        _searchText = text;
        _searchMark = -1;
        _suggestion = null;
        var run = ++_searchRun;
        if (string.IsNullOrWhiteSpace(text))
        {
            _searchResults = [];
            _searching = false;
            return;
        }

        _searching = true;
        try
        {
            var results = await Search.SearchAsync(text, CurrentSearchScope);
            if (run == _searchRun)
            {
                _searchResults = [.. results];
                _error = null;
                _suggestion = results.Count == 0 ? await Search.SuggestAsync(text) : null;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Search failed");
            if (run == _searchRun)
            {
                _searchResults = [];
                _error = T["The search could not be run. Your notes are not affected."];
            }
        }
        finally
        {
            if (run == _searchRun)
            {
                _searching = false;
            }
        }
    }

    private async Task SetSearchScopeAsync(bool scoped)
    {
        _searchScoped = scoped;
        var text = _searchText;
        _searchText = string.Empty;
        await SearchAsync(text);
    }

    private async Task UseSuggestionAsync(string corrected)
    {
        _searchText = string.Empty;
        await SearchAsync(corrected);
        if (_searchBox is not null)
        {
            await _searchBox.SetTextAsync(corrected);
        }
    }

    private void PromptSaveSearch() => OpenPrompt(new PromptRequest(
        T["Save search"], T["Name"], _searchText, T["Save"], 100,
        async name =>
        {
            var saved = Settings.Current.SavedSearches.Where(s => s.Name != name).Append(new SavedSearch(name, _searchText)).ToList();
            await Settings.SaveAsync(Settings.Current with { SavedSearches = saved });
            _notice = T["The search was saved as “{0}”.", name];
        }));

    private async Task RunSavedSearchAsync(int index)
    {
        if (index < 0 || index >= Settings.Current.SavedSearches.Count)
        {
            return;
        }

        var saved = Settings.Current.SavedSearches[index];
        _searchText = string.Empty;
        await SearchAsync(saved.Query);
        if (_searchBox is not null)
        {
            await _searchBox.SetTextAsync(saved.Query);
        }
    }

    private void PromptRenameSavedSearch(int index) => OpenPrompt(new PromptRequest(
        T["Rename saved search"], T["Name"], Settings.Current.SavedSearches[index].Name, T["Rename"], 100,
        async name =>
        {
            var list = Settings.Current.SavedSearches.ToList();
            list[index] = list[index] with { Name = name };
            await Settings.SaveAsync(Settings.Current with { SavedSearches = list });
        }));

    private async Task RemoveSavedSearchAsync(int index)
    {
        var list = Settings.Current.SavedSearches.ToList();
        if (index < 0 || index >= list.Count)
        {
            return;
        }

        list.RemoveAt(index);
        await Settings.SaveAsync(Settings.Current with { SavedSearches = list });
    }

    private void MoveSearchMark(int step)
    {
        if (_selection is NavSelection.Search && _searchResults.Count > 0)
        {
            _searchMark = Math.Clamp(_searchMark + step, 0, _searchResults.Count - 1);
        }
    }

    private async Task OpenMarkedResultAsync()
    {
        if (_selection is NavSelection.Search && _searchResults.Count > 0)
        {
            await OpenAsync(_searchResults[Math.Max(_searchMark, 0)].NoteId);
        }
    }

    /// <summary>Escape in the search box: back to where the user was before searching.</summary>
    private async Task CloseSearchAsync()
    {
        if (_selection is not NavSelection.Search)
        {
            _searchText = string.Empty;
            return;
        }

        if (!await SaveBeforeLeavingAsync())
        {
            return;
        }

        var back = _beforeSearch ?? (Inbox is { } inbox ? new NavSelection.Notebook(inbox.Id) : new NavSelection.AllNotes());
        await RunAsync(T["That could not be opened."], () => GoToAsync(back));
    }

    private void EndSearch()
    {
        _searchRun++;
        _searchText = string.Empty;
        _searchResults = [];
        _searchMark = -1;
        _searching = false;
        _beforeSearch = null;
    }

    // ---- Context menus -------------------------------------------------------------------------

    private void ShowNavMenu((NavSelection Target, MouseEventArgs Mouse) at)
    {
        var items = new List<ContextMenu.Item>();
        switch (at.Target)
        {
            case NavSelection.Notebook notebook when notebook.NotebookId == Inbox?.Id || _notebooks.Any(n => n.Id == notebook.NotebookId && n.IsCalendar):
                items.Add(new(T["New note"], () => NewNoteInAsync(notebook.NotebookId, null)));
                break;
            case NavSelection.Notebook notebook:
                var notebookName = _notebooks.FirstOrDefault(n => n.Id == notebook.NotebookId)?.Name ?? T["Notebook"];
                items.Add(new(T["New note"], () => NewNoteInAsync(notebook.NotebookId, null)));
                items.Add(new(T["New folder"], () => { PromptNewFolderIn(notebook.NotebookId, null, notebookName); return Task.CompletedTask; }));
                items.Add(ContextMenu.Item.Line());
                items.Add(new(T["Move up"], () => MoveNotebookByAsync(notebook.NotebookId, -1)));
                items.Add(new(T["Move down"], () => MoveNotebookByAsync(notebook.NotebookId, 1)));
                items.Add(ContextMenu.Item.Line());
                items.Add(new(T["Rename…"], () => { PromptRenameNotebook(notebook.NotebookId, notebookName); return Task.CompletedTask; }));
                items.Add(new(T["Delete"], () => { ConfirmDeleteNotebook(notebook.NotebookId, notebookName); return Task.CompletedTask; }));
                break;
            case NavSelection.Folder folder:
                var folderName = _folders.FirstOrDefault(f => f.Id == folder.FolderId)?.Name ?? T["Folder"];
                items.Add(new(T["New note"], () => NewNoteInAsync(folder.NotebookId, folder.FolderId)));
                items.Add(new(T["New folder"], () => { PromptNewFolderIn(folder.NotebookId, folder.FolderId, folderName); return Task.CompletedTask; }));
                items.Add(ContextMenu.Item.Line());
                items.Add(new(T["Move up"], () => MoveFolderByAsync(folder.FolderId, -1)));
                items.Add(new(T["Move down"], () => MoveFolderByAsync(folder.FolderId, 1)));
                items.Add(ContextMenu.Item.Line());
                items.Add(new(T["Rename…"], () => { PromptRenameFolder(folder.FolderId, folderName); return Task.CompletedTask; }));
                items.Add(new(T["Move…"], () => { OpenMove(folder.FolderId, isFolder: true, folderName); return Task.CompletedTask; }));
                items.Add(new(T["Delete"], () => { ConfirmDeleteFolder(folder.FolderId, folderName); return Task.CompletedTask; }));
                break;
            case NavSelection.SavedSearch saved:
                items.Add(new(T["Rename…"], () => { PromptRenameSavedSearch(saved.Index); return Task.CompletedTask; }));
                items.Add(new(T["Remove"], () => RemoveSavedSearchAsync(saved.Index)));
                break;
            case NavSelection.Tag tag:
                var tagName = _tags.FirstOrDefault(t => t.Id == tag.TagId)?.Name ?? T["Tag"];
                items.Add(new(T["Rename tag…"], () => { PromptRenameTag(tag.TagId, tagName); return Task.CompletedTask; }));
                items.Add(new(T["Delete tag"], () => { ConfirmDeleteTag(tag.TagId, tagName); return Task.CompletedTask; }));
                break;
            default:
                return;
        }

        _menu = new MenuState(items, at.Mouse.ClientX, at.Mouse.ClientY, T["Actions"]);
    }

    private void ShowNoteMenu((NoteSummary Note, MouseEventArgs Mouse) at)
    {
        var note = at.Note;
        var items = new List<ContextMenu.Item>
        {
            new(T["Open"], () => OpenAsync(note.Id)),
            new(note.IsFavorite ? T["Remove from favorites"] : T["Add to favorites"], () => SetFavoriteAsync(note, !note.IsFavorite)),
            new(note.IsPinned ? T["Unpin"] : T["Pin to top"], () => SetPinnedAsync(note, !note.IsPinned)),
            ContextMenu.Item.Line(),
            new(T["Move…"], () => { OpenMove(note.Id, isFolder: false, note.Title); return Task.CompletedTask; }),
            new(T["Duplicate"], () => DuplicateAsync(note.Id)),
            new(T["Export…"], () => { _menu = null; _export = note; return Task.CompletedTask; }),
            ContextMenu.Item.Line(),
            new(T["Move to trash"], () => DeleteNoteAsync(note.Id)),
        };
        _menu = new MenuState(items, at.Mouse.ClientX, at.Mouse.ClientY, T["Actions for {0}", note.Title]);
    }

    private async Task SetFavoriteAsync(NoteSummary note, bool favorite)
    {
        if (_current?.Id == note.Id)
        {
            await ToggleFavoriteAsync();
            return;
        }

        await RunAsync(T["The favorite could not be changed."], async () =>
        {
            await Notes.SetFavoriteAsync(note.Id, favorite);
            ReplaceSummary(note with { IsFavorite = favorite });
            if (_selection is NavSelection.Favorites && !favorite)
            {
                _notes.RemoveAll(n => n.Id == note.Id);
            }
        });
    }

    private async Task SetPinnedAsync(NoteSummary note, bool pinned)
    {
        if (_current?.Id == note.Id)
        {
            await TogglePinnedAsync();
            return;
        }

        await RunAsync(T["The pin could not be changed."], async () =>
        {
            await Notes.SetPinnedAsync(note.Id, pinned);
            ReplaceSummary(note with { IsPinned = pinned });
            SortNotes();
        });
    }

    private async Task DuplicateAsync(Guid id)
    {
        if (!await SaveBeforeLeavingAsync())
        {
            return;
        }

        await RunAsync(T["The note could not be duplicated."], async () =>
        {
            var copy = await Notes.DuplicateAsync(id);
            if (_selection is NavSelection.Notebook or NavSelection.Folder or NavSelection.AllNotes or NavSelection.Tag)
            {
                _notes.Insert(0, copy.ToSummary());
                SortNotes();
            }

            _current = copy;
            _focusNewNote = true;
            await RecordOpenedAsync(copy.Id);
            await LoadTagsAsync();
            _notice = T["“{0}” was created beside the original.", copy.Title];
        });
    }

    private async Task DeleteNoteAsync(Guid id)
    {
        if (_current?.Id == id)
        {
            await DeleteCurrentNoteAsync();
            return;
        }

        var title = _notes.FirstOrDefault(n => n.Id == id)?.Title ?? T["The note"];
        if (Settings.Current.ConfirmDeletion && !_confirmedDeletion)
        {
            OpenConfirm(new ConfirmRequest(
                T["Move “{0}” to the trash?", title], T["You can restore it from the Trash later."], T["Move to trash"], Danger: false,
                async () =>
                {
                    _confirmedDeletion = true;
                    try
                    {
                        await DeleteNoteAsync(id);
                    }
                    finally
                    {
                        _confirmedDeletion = false;
                    }
                }));
            return;
        }

        await RunAsync(T["The note could not be moved to the trash."], async () =>
        {
            await Trash.MoveToTrashAsync(id);
            _notes.RemoveAll(n => n.Id == id);
            _treeNotes.RemoveAll(n => n.Id == id);
            _searchResults.RemoveAll(r => r.NoteId == id);
            await LoadTagsAsync();
            _notice = T["“{0}” was moved to the trash.", title];
        });
    }

    private async Task NewNoteInAsync(Guid notebookId, Guid? parentId)
    {
        NavSelection target = parentId is { } folder ? new NavSelection.Folder(notebookId, folder) : new NavSelection.Notebook(notebookId);
        if (target != _selection)
        {
            await SelectAsync(target);
        }

        await NewNoteAsync();
    }

    // ---- Moving --------------------------------------------------------------------------------

    /// <summary>Opens the destination picker. A folder cannot go into itself or anything below it.</summary>
    private void OpenMove(Guid nodeId, bool isFolder, string name)
    {
        var excluded = new HashSet<Guid>();
        if (isFolder)
        {
            var pending = new Queue<Guid>([nodeId]);
            while (pending.TryDequeue(out var id) && excluded.Add(id))
            {
                foreach (var child in _folders.Where(f => f.ParentId == id))
                {
                    pending.Enqueue(child.Id);
                }
            }
        }

        _dialogError = null;
        _menu = null;
        _move = new MoveRequest(nodeId, isFolder, name, excluded);
    }

    private async Task ConfirmMoveAsync((Guid NotebookId, Guid? ParentId) destination)
    {
        if (_move is not { } move || !await SaveBeforeLeavingAsync())
        {
            return;
        }

        try
        {
            await MoveAsync(move.NodeId, move.IsFolder, move.Name, destination.NotebookId, destination.ParentId);
            _move = null;
        }
        catch (DomainException ex)
        {
            _dialogError = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Move failed");
            _dialogError = T["That did not work. Nothing was moved."];
        }
    }

    private async Task MoveAsync(Guid nodeId, bool isFolder, string name, Guid notebookId, Guid? parentId)
    {
        await Tree.MoveAsync(nodeId, notebookId, parentId);
        await LoadStructureAsync();
        await LoadListAsync(openFirst: false);
        if (_current is not null && !isFolder && _current.Id == nodeId)
        {
            _current = await Notes.GetAsync(nodeId);
        }
        else if (_current is not null && _notes.Count > 0 && _notes.All(n => n.Id != _current.Id) && _selection is NavSelection.Notebook or NavSelection.Folder)
        {
            _current = null;
        }

        var place = parentId is { } folder
            ? _folders.FirstOrDefault(f => f.Id == folder)?.Name
            : _notebooks.FirstOrDefault(n => n.Id == notebookId)?.Name;
        _notice = T["“{0}” was moved to {1}.", name, place ?? T["its new place"]];
    }

    /// <summary>A note or folder was dropped on a notebook or folder in the navigation.</summary>
    private async Task<string> DropZoneAsync()
    {
        try
        {
            return _shortcutsModule is null ? "into" : await _shortcutsModule.InvokeAsync<string>("lastDropZone");
        }
        catch (JSException)
        {
            return "into";
        }
    }

    private async Task DropAsync(NavSelection target)
    {
        // Taken before anything is awaited: the drag's end arrives meanwhile and clears them.
        var draggingNotebook = _draggingNotebook;
        var draggingItem = _dragging;
        _draggingNotebook = null;
        _dragging = null;
        var zone = await DropZoneAsync();
        if (draggingNotebook is { } movingNotebook)
        {
            if (zone != "into" && target is NavSelection.Notebook beside && beside.NotebookId != movingNotebook)
            {
                await ReorderNotebookAsync(movingNotebook, beside.NotebookId, after: zone == "after");
            }
            else
            {
                ConfirmNotebookMove(movingNotebook, target);
            }

            return;
        }

        if (draggingItem is not { } dragged)
        {
            return;
        }

        if (dragged.IsFolder && zone != "into" && target is NavSelection.Folder sibling && sibling.FolderId != dragged.Id)
        {
            // On the edge of a folder: beside it, in the same place, before or after it.
            await PlaceFolderBesideAsync(dragged.Id, sibling.FolderId, after: zone == "after");
            return;
        }

        var (notebookId, parentId) = target switch
        {
            NavSelection.Folder folder => (folder.NotebookId, (Guid?)folder.FolderId),
            NavSelection.Notebook notebook => (notebook.NotebookId, null),
            _ => (Guid.Empty, null),
        };
        if (notebookId == Guid.Empty || parentId == dragged.Id)
        {
            return;
        }

        var name = dragged.IsFolder
            ? _folders.FirstOrDefault(f => f.Id == dragged.Id)?.Name ?? T["Folder"]
            : _notes.FirstOrDefault(n => n.Id == dragged.Id)?.Title ?? _current?.Title ?? T["Note"];
        if (!await SaveBeforeLeavingAsync())
        {
            return;
        }

        await RunAsync(T["That could not be moved."], () => MoveAsync(dragged.Id, dragged.IsFolder, name, notebookId, parentId));
    }

    // ---- Notebooks and folders -----------------------------------------------------------------

    /// <summary>Folders beside one another, in the order the tree shows them.</summary>
    private List<FolderSummary> SiblingsOf(Guid notebookId, Guid? parentId) =>
        [.. _folders.Where(f => f.NotebookId == notebookId && f.ParentId == parentId).OrderBy(f => f.SortOrder).ThenBy(f => f.Name, StringComparer.CurrentCulture)];

    private async Task PlaceFolderBesideAsync(Guid folderId, Guid besideId, bool after)
    {
        var beside = _folders.FirstOrDefault(f => f.Id == besideId);
        var moving = _folders.FirstOrDefault(f => f.Id == folderId);
        if (beside is null || moving is null)
        {
            return;
        }

        var siblings = SiblingsOf(beside.NotebookId, beside.ParentId).Where(f => f.Id != folderId).ToList();
        var index = siblings.FindIndex(f => f.Id == besideId) + (after ? 1 : 0);
        await RunAsync(T["That could not be moved."], async () =>
        {
            await Tree.MoveAsync(folderId, beside.NotebookId, beside.ParentId, index);
            await LoadStructureAsync();
        });
    }

    /// <summary>Up (-1) or down (+1) among the folders beside it.</summary>
    private async Task MoveFolderByAsync(Guid folderId, int step)
    {
        var folder = _folders.FirstOrDefault(f => f.Id == folderId);
        if (folder is null)
        {
            return;
        }

        var siblings = SiblingsOf(folder.NotebookId, folder.ParentId);
        var index = siblings.FindIndex(f => f.Id == folderId) + step;
        if (index < 0 || index >= siblings.Count)
        {
            return;
        }

        await RunAsync(T["That could not be moved."], async () =>
        {
            await Tree.MoveAsync(folderId, folder.NotebookId, folder.ParentId, index);
            await LoadStructureAsync();
        });
    }

    private List<NotebookSummary> OrderedUserNotebooks => [.. _notebooks.Where(n => !n.IsInbox && !n.IsCalendar)];

    private async Task ReorderNotebookAsync(Guid notebookId, Guid besideId, bool after)
    {
        var others = OrderedUserNotebooks.Where(n => n.Id != notebookId).ToList();
        var index = others.FindIndex(n => n.Id == besideId);
        if (index < 0)
        {
            return;
        }

        await RunAsync(T["That could not be moved."], async () =>
        {
            await Notebooks.ReorderAsync(notebookId, index + (after ? 1 : 0));
            await LoadStructureAsync();
        });
    }

    private async Task MoveNotebookByAsync(Guid notebookId, int step)
    {
        var index = OrderedUserNotebooks.FindIndex(n => n.Id == notebookId) + step;
        if (index < 0 || index >= OrderedUserNotebooks.Count)
        {
            return;
        }

        await RunAsync(T["That could not be moved."], async () =>
        {
            await Notebooks.ReorderAsync(notebookId, index);
            await LoadStructureAsync();
        });
    }

    /// <summary>A notebook was dropped on another notebook or a folder: ask, then make it a folder there.</summary>
    private void ConfirmNotebookMove(Guid notebookId, NavSelection target)
    {
        var (targetNotebookId, targetFolderId) = target switch
        {
            NavSelection.Folder folder => (folder.NotebookId, (Guid?)folder.FolderId),
            NavSelection.Notebook notebook => (notebook.NotebookId, null),
            _ => (Guid.Empty, null),
        };
        if (targetNotebookId == Guid.Empty || targetNotebookId == notebookId)
        {
            return;
        }

        var name = NotebookName(notebookId);
        var place = targetFolderId is { } folderId ? FolderName(folderId) : NotebookName(targetNotebookId);
        OpenConfirm(new ConfirmRequest(
            T["Move the notebook “{0}” into “{1}”?", name, place],
            T["“{0}” becomes a folder inside “{1}”, with all its notes and folders as they are. It will no longer be a notebook of its own.", name, place],
            T["Move it there"],
            Danger: false,
            async () =>
            {
                if (!await SaveBeforeLeavingAsync())
                {
                    return;
                }

                var folder = await Tree.MoveNotebookIntoAsync(notebookId, targetNotebookId, targetFolderId);
                await LoadStructureAsync();
                await GoToAsync(new NavSelection.Folder(folder.NotebookId, folder.Id));
                _notice = T["“{0}” is now a folder in “{1}”.", name, place];
            }));
    }

    private void PromptNewNotebook() => OpenPrompt(new PromptRequest(
        T["New notebook"], T["Name"], string.Empty, T["Create"], Notebook.MaxNameLength,
        async name =>
        {
            var notebook = await Notebooks.CreateAsync(name);
            _firstRun = false;
            await LoadStructureAsync();
            await GoToAsync(new NavSelection.Notebook(notebook.Id));
        }));

    /// <summary>The whole data set was replaced from a backup: start over as if the app had just opened.</summary>
    private async Task AfterRestoreAsync()
    {
        await RunAsync(T["The restored data could not be shown. Restart WorriorVex."], async () =>
        {
            _current = null;
            _notes = [];
            EndSearch();
            await LoadStructureAsync();
            _selection = new NavSelection.Data();
            _notice = T["The backup was restored. Everything you see now comes from it."];
        });
    }

    private async Task ExportNoteAsync(NoteSummary note, ExportFormat format)
    {
        _export = null;
        if (!await SaveBeforeLeavingAsync())
        {
            return;
        }

        var suggested = ExportService_SafeName(note.Title) + IExportService.Extension(format);
        var path = await Shell.PickSaveLocationAsync(T["Export “{0}”", note.Title], suggested);
        if (path is null)
        {
            return;
        }

        await RunAsync(T["The note could not be exported."], async () =>
        {
            var result = await Exporter.ExportNoteAsync(note.Id, format, path);
            _notice = result.Problems.Count == 0
                ? T["“{0}” was exported to {1}.", note.Title, result.Path]
                : T["“{0}” was exported to {1}, with problems: {2}", note.Title, result.Path, string.Join(" ", result.Problems)];
        });
    }

    /// <summary>A file name from a title: no path separators or characters file systems refuse.</summary>
    private static string ExportService_SafeName(string title)
    {
        var cleaned = Domain.Attachment.CleanFileName(title.Replace('/', '-').Replace('\\', '-'));
        return cleaned.Length > 120 ? cleaned[..120].TrimEnd() : cleaned;
    }

    /// <summary>A KeepNote notebook was imported (or an import stopped half-way): show what is there now.</summary>
    private async Task AfterImportAsync(Guid notebookId)
    {
        _firstRun = false;
        await RunAsync(T["The imported notebook could not be shown."], async () =>
        {
            await LoadStructureAsync();
            if (notebookId != Guid.Empty && _notebooks.Any(n => n.Id == notebookId))
            {
                _notice = T["“{0}” was imported.", _notebooks.First(n => n.Id == notebookId).Name];
            }
        });
    }

    private void PromptNewFolder()
    {
        switch (_selection)
        {
            case NavSelection.Folder folder:
                PromptNewFolderIn(folder.NotebookId, folder.FolderId, Heading);
                break;
            case NavSelection.Notebook notebook when CanHoldFolders:
                PromptNewFolderIn(notebook.NotebookId, null, Heading);
                break;
        }
    }

    /// <summary>The + on a notebook or folder row.</summary>
    /// <summary>The + on a notebook or folder row: a small menu to add a note or a folder inside it.</summary>
    private void AddBranch((Guid NotebookId, Guid? ParentId, MouseEventArgs Mouse) place)
    {
        var name = (place.ParentId is { } folderId
            ? _folders.FirstOrDefault(f => f.Id == folderId)?.Name
            : _notebooks.FirstOrDefault(n => n.Id == place.NotebookId)?.Name) ?? T["this place"];
        var items = new List<ContextMenu.Item>
        {
            new(T["New note"], () => NewNoteInAsync(place.NotebookId, place.ParentId)),
            new(T["New folder"], () => { PromptNewFolderIn(place.NotebookId, place.ParentId, name); return Task.CompletedTask; }),
        };
        _menu = new MenuState(items, place.Mouse.ClientX, place.Mouse.ClientY, T["Add to {0}", name]);
    }

    /// <summary>The + on a note row: a note cannot hold notes, so it becomes a folder of its name with the note inside, then the new thing goes in there.</summary>
    private void AddUnderNote((NoteSummary Note, MouseEventArgs Mouse) at)
    {
        var items = new List<ContextMenu.Item>
        {
            new(T["New note under it"], () => GrowFromNoteAsync(at.Note, addFolder: false)),
            new(T["New folder under it"], () => GrowFromNoteAsync(at.Note, addFolder: true)),
        };
        _menu = new MenuState(items, at.Mouse.ClientX, at.Mouse.ClientY, T["Grow “{0}” into a folder", at.Note.Title]);
    }

    private async Task GrowFromNoteAsync(NoteSummary note, bool addFolder)
    {
        if (!await SaveBeforeLeavingAsync())
        {
            return;
        }

        await RunAsync(T["The note could not be turned into a folder."], async () =>
        {
            // The same name, in the same place; the note itself moves inside as its first entry.
            var folder = await Tree.CreateFolderAsync(note.NotebookId, note.ParentId, note.Title);
            await Tree.MoveAsync(note.Id, note.NotebookId, folder.Id);
            await LoadStructureAsync();
            _notice = T["“{0}” is now a folder with the note inside it.", note.Title];
            if (addFolder)
            {
                await SelectAsync(new NavSelection.Folder(note.NotebookId, folder.Id));
                PromptNewFolderIn(note.NotebookId, folder.Id, note.Title);
            }
            else
            {
                await NewNoteInAsync(note.NotebookId, folder.Id);
            }
        });
    }

    /// <summary>A note was chosen in the tree: its folder becomes the place, so the list and "+ New" follow it.</summary>
    private async Task OpenFromTreeAsync(Guid id)
    {
        var note = _treeNotes.FirstOrDefault(n => n.Id == id);
        if (note is not null)
        {
            NavSelection place = note.ParentId is { } parent ? new NavSelection.Folder(note.NotebookId, parent) : new NavSelection.Notebook(note.NotebookId);
            if (place != _selection)
            {
                if (!await SaveBeforeLeavingAsync())
                {
                    return;
                }

                EndSearch();
                _selection = place;
                _notice = null;
                await LoadListAsync(openFirst: false);
                await RememberPlaceAsync();
            }
        }

        await OpenAsync(id);
    }

    private void PromptNewFolderIn(Guid notebookId, Guid? parentId, string placeName) => OpenPrompt(new PromptRequest(
        T["New folder"], T["Name of the folder in “{0}”", placeName], string.Empty, T["Create"], Node.MaxNameLength,
        async name =>
        {
            var folder = await Tree.CreateFolderAsync(notebookId, parentId, name);
            await LoadStructureAsync();
            await GoToAsync(new NavSelection.Folder(folder.NotebookId, folder.Id));
        }));

    private void PromptRename()
    {
        switch (_selection)
        {
            case NavSelection.Folder folder:
                PromptRenameFolder(folder.FolderId, Heading);
                break;
            case NavSelection.Notebook notebook when CanChangeContainer:
                PromptRenameNotebook(notebook.NotebookId, Heading);
                break;
        }
    }

    private void PromptRenameFolder(Guid folderId, string currentName) => OpenPrompt(new PromptRequest(
        T["Rename folder"], T["Name"], currentName, T["Rename"], Node.MaxNameLength,
        async name =>
        {
            await Tree.RenameFolderAsync(folderId, name);
            await LoadStructureAsync();
        }));

    private void PromptRenameNotebook(Guid notebookId, string currentName) => OpenPrompt(new PromptRequest(
        T["Rename notebook"], T["Name"], currentName, T["Rename"], Notebook.MaxNameLength,
        async name =>
        {
            await Notebooks.RenameAsync(notebookId, name);
            await LoadStructureAsync();
        }));

    private void PromptRenameTag(Guid tagId, string currentName) => OpenPrompt(new PromptRequest(
        T["Rename tag"], T["Name"], currentName, T["Rename"], Domain.Tag.MaxNameLength,
        async name =>
        {
            await TagService.RenameAsync(tagId, name);
            await LoadTagsAsync();
        }));

    private void ConfirmDeleteContainer()
    {
        switch (_selection)
        {
            case NavSelection.Folder folder:
                ConfirmDeleteFolder(folder.FolderId, Heading);
                break;
            case NavSelection.Notebook notebook when CanChangeContainer:
                ConfirmDeleteNotebook(notebook.NotebookId, Heading);
                break;
        }
    }

    private void ConfirmDeleteFolder(Guid folderId, string name) => OpenConfirm(new ConfirmRequest(
        T["Delete the folder “{0}” and everything in it?", name],
        T["This is the whole folder, not one note: {0} and all its notes and sub-folders move to the trash. You can restore them from there.", CountInside(folderId)],
        T["Delete the whole folder"],
        Danger: true,
        async () =>
        {
            await Trash.MoveToTrashAsync(folderId);
            await AfterContainerDeletedAsync(name);
        }));

    private void ConfirmDeleteNotebook(Guid notebookId, string name) => OpenConfirm(new ConfirmRequest(
        T["Delete the notebook “{0}” and everything in it?", name],
        T["This is the whole notebook, not one note: {0} and all its notes and folders move to the trash. You can restore them from there.", CountInside(notebookId, isNotebook: true)],
        T["Delete the whole notebook"],
        Danger: true,
        async () =>
        {
            await Trash.MoveNotebookToTrashAsync(notebookId);
            await AfterContainerDeletedAsync(name);
        }));

    private void ConfirmDeleteTag(Guid tagId, string name) => OpenConfirm(new ConfirmRequest(
        T["Delete tag “{0}”?", name],
        T["The tag is removed from every note that carries it. The notes themselves stay as they are."],
        T["Delete tag"],
        Danger: true,
        async () =>
        {
            await TagService.DeleteAsync(tagId);
            await LoadTagsAsync();
            if (_selection is NavSelection.Tag current && current.TagId == tagId)
            {
                await GoToAsync(new NavSelection.AllNotes());
            }

            _notice = T["The tag “{0}” was deleted.", name];
        }));

    /// <summary>"3 notes and 2 folders": what a container holds, so the person sees the size of what they are about to delete.</summary>
    private string CountInside(Guid id, bool isNotebook = false)
    {
        var folderIds = new HashSet<Guid>();
        var pending = new Queue<Guid>(isNotebook ? _folders.Where(f => f.NotebookId == id && f.ParentId is null).Select(f => f.Id) : [id]);
        while (pending.TryDequeue(out var folder) && folderIds.Add(folder))
        {
            foreach (var child in _folders.Where(f => f.ParentId == folder))
            {
                pending.Enqueue(child.Id);
            }
        }

        var folders = isNotebook ? folderIds.Count : folderIds.Count - 1;
        if (!Settings.Current.ShowNotesInTree)
        {
            // Without the tree's note list only the folders are known here.
            return T["{0} folder(s)", folders];
        }

        var notes = _treeNotes.Count(n => isNotebook ? n.NotebookId == id : n.ParentId is { } parent && folderIds.Contains(parent));
        return T["{0} note(s) and {1} folder(s)", notes, folders];
    }

    private async Task AfterContainerDeletedAsync(string name)
    {
        await LoadStructureAsync();
        await GoToAsync(Inbox is { } inbox ? new NavSelection.Notebook(inbox.Id) : new NavSelection.AllNotes());
        _notice = T["“{0}” was moved to the trash.", name];
    }

    private async Task GoToAsync(NavSelection selection)
    {
        EndSearch();
        _selection = selection;
        _notice = null;
        await LoadListAsync(openFirst: true);
    }

    // ---- Trash ---------------------------------------------------------------------------------

    private async Task RestoreAsync(TrashItem item)
    {
        await RunAsync(T["The item could not be restored."], async () =>
        {
            var result = await Trash.RestoreAsync(item.Id);
            await LoadStructureAsync();
            await LoadListAsync(openFirst: false);

            var notebook = _notebooks.FirstOrDefault(n => n.Id == result.NotebookId)?.Name ?? "its notebook";
            _notice = result.Relocated
                ? T["“{0}” was restored to the top of {1}, because the place it was in is gone.", item.Name, notebook]
                : T["“{0}” was restored.", item.Name];
        });
    }

    private void ConfirmDeletePermanently(TrashItem item) => OpenConfirm(new ConfirmRequest(
        T["Delete “{0}” permanently?", item.Name],
        item.Kind == TrashItemKind.Note
            ? T["The note, its history and its attachments are removed for good. This cannot be undone."]
            : T["It and everything that was deleted with it, including attachments, are removed for good. This cannot be undone."],
        T["Delete permanently"],
        Danger: true,
        async () =>
        {
            await Trash.DeletePermanentlyAsync(item.Id);
            await LoadListAsync(openFirst: false);
            _notice = T["“{0}” was deleted permanently.", item.Name];
        }));

    private void ConfirmEmptyTrash() => OpenConfirm(new ConfirmRequest(
        T["Empty the trash?"],
        T["{0} item(s) and everything in them, including attachments, are removed for good. This cannot be undone.", _trash.Count],
        T["Empty trash"],
        Danger: true,
        async () =>
        {
            var removed = await Trash.EmptyAsync();
            await LoadListAsync(openFirst: false);
            _notice = T["The trash was emptied: {0} item(s) removed.", removed];
        }));

    // ---- Dialogs and errors --------------------------------------------------------------------

    private void OpenPrompt(PromptRequest request)
    {
        _dialogError = null;
        _confirm = null;
        _move = null;
        _menu = null;
        _prompt = request;
    }

    private void OpenConfirm(ConfirmRequest request)
    {
        _dialogError = null;
        _prompt = null;
        _move = null;
        _menu = null;
        _confirm = request;
    }

    private void CloseDialogs()
    {
        _prompt = null;
        _confirm = null;
        _move = null;
        _menu = null;
        _dialogError = null;
    }

    /// <summary>A refused name keeps the dialog open with the reason, so it can be corrected in place.</summary>
    private async Task ConfirmPromptAsync(string value)
    {
        if (_prompt is not { } prompt || !await SaveBeforeLeavingAsync())
        {
            return;
        }

        try
        {
            await prompt.OnConfirm(value);
            CloseDialogs();
        }
        catch (DomainException ex)
        {
            _dialogError = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Dialog} failed", prompt.Title);
            _dialogError = T["That did not work. Nothing was changed."];
        }
    }

    private async Task ConfirmActionAsync()
    {
        if (_confirm is not { } confirm)
        {
            return;
        }

        CloseDialogs();
        if (await SaveBeforeLeavingAsync())
        {
            await RunAsync(T["That did not work. Nothing was removed."], confirm.OnConfirm);
        }
    }

    /// <summary>Runs an action; a broken rule is shown in its own words, anything else is logged and summarised.</summary>
    private async Task RunAsync(string failureMessage, Func<Task> action)
    {
        try
        {
            _error = null;
            await action();
        }
        catch (DomainException ex)
        {
            _error = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Failure}", failureMessage);
            _error = failureMessage;
        }
    }

    // ---- Autosave events -----------------------------------------------------------------------

    private void OnSaveStateChanged() => _ = InvokeAsync(StateHasChanged);

    private void OnNoteSaved(NoteDetail saved) => _ = InvokeAsync(() =>
    {
        _connectionsVersion++;
        if (_current?.Id == saved.Id)
        {
            _current = _current with { Content = saved.Content, UpdatedAt = saved.UpdatedAt, Title = saved.Title };
        }

        var index = _notes.FindIndex(n => n.Id == saved.Id);
        if (index < 0)
        {
            StateHasChanged();
            return;
        }

        var summary = _notes[index] with { Title = saved.Title, UpdatedAt = saved.UpdatedAt };
        if (_notes[index] == summary)
        {
            StateHasChanged();
            return;
        }

        _notes[index] = summary;
        SortNotes();
        var treeIndex = _treeNotes.FindIndex(n => n.Id == saved.Id);
        if (treeIndex >= 0)
        {
            _treeNotes[treeIndex] = _treeNotes[treeIndex] with { Title = saved.Title, UpdatedAt = saved.UpdatedAt };
            _treeNotes = [.. Sorted(_treeNotes)];
        }

        StateHasChanged();
    });

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _stopping.CancelAsync();
        Autosaver.StateChanged -= OnSaveStateChanged;
        Autosaver.NoteSaved -= OnNoteSaved;
        Settings.Changed -= OnSettingsChanged;
        T.Changed -= OnLanguageChanged;
        BackupScheduler.Completed -= OnAutoBackup;

        try
        {
            if (_shortcuts is not null)
            {
                await _shortcuts.InvokeVoidAsync("dispose");
                await _shortcuts.DisposeAsync();
            }

            foreach (var handle in new[] { _navResizerHandle, _listResizerHandle, _treeKeysHandle })
            {
                if (handle is not null)
                {
                    await handle.InvokeVoidAsync("dispose");
                    await handle.DisposeAsync();
                }
            }

            if (_shortcutsModule is not null)
            {
                await _shortcutsModule.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or ObjectDisposedException)
        {
            // The page is going away; whatever is left on it goes with it.
        }

        _self?.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record PromptRequest(string Title, string Label, string InitialValue, string ConfirmText, int MaxLength, Func<string, Task> OnConfirm);

    private sealed record ConfirmRequest(string Title, string Message, string ConfirmText, bool Danger, Func<Task> OnConfirm);

    private sealed record MoveRequest(Guid NodeId, bool IsFolder, string Name, IReadOnlySet<Guid> Excluded);

    private sealed record MenuState(IReadOnlyList<ContextMenu.Item> Items, double X, double Y, string Label);
}
