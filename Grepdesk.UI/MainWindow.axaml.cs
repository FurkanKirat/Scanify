using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Grepdesk.Core;
using Grepdesk.Core.ContentSearch;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grepdesk.Core.Editor;
using Grepdesk.UI.Helpers;

namespace Grepdesk.UI;

public partial class MainWindow : Window
{
    private readonly IPlatformShell _shell = PlatformShellFactory.CreatePlatformShell();
    private readonly EditorDetector _editorDetector;
    
    // ---- shared file-name index (tab 1) ----
    private readonly FileIndex _index = new();
    private readonly ObservableCollection<ResultItem> _results = [];
    private CancellationTokenSource _indexCts = new();
    private CancellationTokenSource _searchCts = new();
    private string _lastQuery = "";
    private List<string>? _selectedRoots; // null = whole PC

    // ---- content search (tab 2) ----
    private readonly ObservableCollection<ResultItem> _contentResults = [];
    private CancellationTokenSource _contentSearchCts = new();
    private string? _contentSearchFolder;

    public MainWindow()
    {
        InitializeComponent();
        _editorDetector = new EditorDetector(_shell);

        // --- Tab 1 wiring ---
        ResultsList.ItemsSource = _results;

        SearchBox.TextChanged += OnSearchTextChanged;
        SearchBox.KeyDown += OnSearchKeyDown;
        ResultsList.DoubleTapped += OnAnyResultDoubleTapped;
        ResultsList.PointerReleased += OnAnyResultRightClick;

        ChooseFolderButton.Click += async (_, _) => await ChooseFolderAsync();
        ScanAllButton.Click += async (_, _) => await ScanWholeComputerAsync();
        ReindexButton.Click += async (_, _) => await StartIndexingAsync();

        _index.ProgressChanged += count =>
            Dispatcher.UIThread.Post(() =>
                StatusText.Text = $"Indexing... {count:N0} files");

        _index.IndexingComplete += () =>
            Dispatcher.UIThread.Post(() =>
            {
                StatusText.Text = $"Ready — {_index.Count:N0} items indexed";
                ReindexButton.IsEnabled = true;
            });

        // No automatic scan on launch — user picks a folder or "scan whole PC" first.

        // --- Tab 2 wiring ---
        ContentResultsList.ItemsSource = _contentResults;
        ContentResultsList.DoubleTapped += OnAnyResultDoubleTapped;
        ContentResultsList.PointerReleased += OnAnyResultRightClick;
        ContentChooseFolderButton.Click += async (_, _) => await ChooseContentFolderAsync();
        ContentSearchBox.KeyDown += OnContentSearchKeyDown;
    }

    // =====================================================================
    // TAB 1 — file name search
    // =====================================================================

    private async Task ChooseFolderAsync()
    {
        var provider = StorageProvider;
        if (provider is null) return;

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Taranacak klasörü seç",
            AllowMultiple = true
        });

        if (folders.Count == 0) return;

        var paths = folders
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        if (paths.Count == 0) return;

        _selectedRoots = paths;
        await StartIndexingAsync();
    }

    private async Task ScanWholeComputerAsync()
    {
        _selectedRoots = null;
        await StartIndexingAsync();
    }

    private async Task StartIndexingAsync()
    {
        _indexCts.Cancel();
        _indexCts = new CancellationTokenSource();
        StatusText.Text = "Indexing...";
        ReindexButton.IsEnabled = false;
        _results.Clear();

        await _index.BuildIndexAsync(_selectedRoots, _indexCts.Token);
        await Search(SearchBox.Text ?? "");
    }

    private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text ?? "";
        if (query == _lastQuery) return;
        await Search(query);
    }
    
    private async Task Search(string query)
    {
        _lastQuery = query;

        await _searchCts.CancelAsync();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        try
        {
            await Task.Delay(150, token);
            if (token.IsCancellationRequested) return;

            RunSearch(query);
        }
        catch (TaskCanceledException) { }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _results.Count > 0)
        {
            ResultsList.Focus();
            ResultsList.SelectedIndex = 0;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
        }
    }

    private void RunSearch(string query)
    {
        _results.Clear();

        if (string.IsNullOrWhiteSpace(query))
        {
            StatusText.Text = _index.Count > 0
                ? $"Ready — {_index.Count:N0} items indexed"
                : "Taramak için bir seçenek belirleyin";
            return;
        }

        if (_index.IsIndexing)
        {
            StatusText.Text = "Still indexing, please wait...";
            return;
        }

        if (_index.Count == 0)
        {
            StatusText.Text = "Önce bir klasör seçin veya tüm PC'yi tarayın";
            return;
        }

        var results = _index.Search(query).ToList();

        foreach (var r in results)
            _results.Add(new ResultItem(r));

        StatusText.Text = results.Count >= 500
            ? $"Showing first 500 results for \"{query}\""
            : $"{results.Count} results for \"{query}\"";
    }
    
    private void OnAnyResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: ResultItem item }) return;
        var result = _shell.OpenPath(item.Result.FullPath);
        ReportShellResult(result, "Dosya açılamadı");
    }

    private void OnAnyResultRightClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (sender is not ListBox list) return;

        // Sağ tıklanan öğeyi bul ve seç
        if (e.Source is Control { DataContext: ResultItem item })
        {
            list.SelectedItem = item;

            var menuItems = new List<Control>();

            var openItem = new MenuItem { Header = "Aç" };
            openItem.Click += (_, _) =>
            {
                var result = _shell.OpenPath(item.Result.FullPath);
                ReportShellResult(result, "Dosya açılamadı");
            };

            var showItem = new MenuItem { Header = "Klasörde Göster" };
            showItem.Click += (_, _) =>
            {
                var result = _shell.ShowInFileManager(item.Result.FullPath);
                ReportShellResult(result, "Klasörde gösterilemedi");
            };

            var copyItem = new MenuItem { Header = "Yolu Kopyala" };
            copyItem.Click += async (_, _) =>
            {
                var clipboard = TopLevel.GetTopLevel(list)?.Clipboard;
                if (clipboard is not null)
                {
                    await clipboard.SetTextAsync(item.Result.FullPath);
                    StatusText.Text = "Yol kopyalandı";
                }
            };

            var terminalItem = new MenuItem { Header = "Terminalde Aç" };
            terminalItem.Click += (_, _) =>
            {
                var res = item.Result;
                var result = _shell.OpenInTerminal(res.IsDirectory ? res.FullPath : res.Directory);
                ReportShellResult(result, "Terminal açılamadı");
            };

            menuItems.Add(openItem);
            menuItems.Add(showItem);
            menuItems.Add(copyItem);

            menuItems.Add(new Separator());
            menuItems.Add(terminalItem);

            if (_editorDetector.AvailableEditors.Count > 0)
            {
                menuItems.Add(new Separator());

                foreach (var editor in _editorDetector.AvailableEditors)
                {
                    if (!EditorTargetResolver.IsCompatible(editor, item.Result.FullPath))
                        continue;

                    var editorItem = new MenuItem { Header = editor.Header };
                    editorItem.Click += (_, _) =>
                    {
                        var result = _shell.OpenInEditor(editor, item.Result.FullPath);
                        ReportShellResult(result, $"{editor.Header} başarısız oldu");
                    };
                    menuItems.Add(editorItem);
                }
            }

            var menu = new ContextMenu
            {
                ItemsSource = menuItems
            };

            menu.Open(list);
        }
    }
    
    private void ReportShellResult(ShellActionResult result, string failureMessagePrefix)
    {
        StatusText.Text = result.IsSuccess
            ? StatusText.Text // başarılıysa mevcut durumu bozma, sessiz geç
            : result.Exception is not null
                ? $"{failureMessagePrefix}: {result.Exception.Message}"
                : $"{failureMessagePrefix} ({result.Status})";
    }
    
    // =====================================================================
    // TAB 2 — content search (searches inside file contents, not just names)
    // =====================================================================

    private async Task ChooseContentFolderAsync()
    {
        var provider = StorageProvider;
        if (provider is null) return;

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "İçinde arama yapılacak klasörü seç",
            AllowMultiple = false
        });

        if (folders.Count == 0) return;

        var path = folders[0].TryGetLocalPath();
        if (path is null) return;

        _contentSearchFolder = path;
        ContentFolderText.Text = path;
        ContentStatusText.Text = "Hazır — arama yapmak için metin girip Enter'a basın";
    }

    private async void OnContentSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        await RunContentSearchAsync(ContentSearchBox.Text ?? "");
    }

    private async Task RunContentSearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        if (_contentSearchFolder is null)
        {
            ContentStatusText.Text = "Önce aranacak klasörü seçin";
            return;
        }

        _contentSearchCts.Cancel();
        _contentSearchCts = new CancellationTokenSource();
        var token = _contentSearchCts.Token;

        _contentResults.Clear();
        ContentStatusText.Text = "Dosyalar taranıyor...";

        // Walk the chosen folder directly — content search doesn't depend on
        // tab 1's name index, so it works even if that index was never built.
        var candidates = EnumerateSearchableFiles(_contentSearchFolder);

        var matchCount = 0;
        var skippedCount = 0; // too large, or extraction failed (corrupt/locked/unsupported-compression)

        void OnSkipped(SkippedFile _)
        {
            Interlocked.Increment(ref skippedCount);
            Dispatcher.UIThread.Post(() =>
                ContentStatusText.Text = $"Taranıyor... {matchCount} eşleşme, {skippedCount} dosya okunamadı");
        }

        try
        {
            await foreach (var match in ContentSearcher.SearchAsync(candidates, query, OnSkipped, token))
            {
                _contentResults.Add(new ResultItem(new SearchResult(match.FullPath), match.Snippet));
                matchCount++;
                ContentStatusText.Text = skippedCount == 0
                    ? $"Taranıyor... {matchCount} eşleşme bulundu"
                    : $"Taranıyor... {matchCount} eşleşme, {skippedCount} dosya okunamadı";
            }

            if (!token.IsCancellationRequested)
            {
                var summary = matchCount == 0 ? "Eşleşme bulunamadı" : $"{matchCount} eşleşme bulundu";
                ContentStatusText.Text = skippedCount == 0
                    ? summary
                    : $"{summary} — {skippedCount} dosya okunamadı (bozuk/kilitli/desteklenmeyen sıkıştırma)";
            }
        }
        catch (OperationCanceledException) { }
    }

    private static IEnumerable<string> EnumerateSearchableFiles(string root)
    {
        var extensions = new HashSet<string>(ExtractorRegistry.AllSupportedExtensions, System.StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            IEnumerable<string> files = System.Array.Empty<string>();
            try { files = Directory.EnumerateFiles(dir).ToList(); }
            catch { /* locked / unauthorized — skip this directory's files */ }

            foreach (var f in files)
                if (extensions.Contains(Path.GetExtension(f)))
                    yield return f;

            IEnumerable<string> subDirs = System.Array.Empty<string>();
            try { subDirs = Directory.EnumerateDirectories(dir).ToList(); }
            catch { /* locked / unauthorized — skip subdirectories */ }

            foreach (var sub in subDirs)
                pending.Push(sub);
        }
    }
}

// ViewModel wrapper for list items — used by both tabs, Snippet only populated
// by content search results.
public class ResultItem(SearchResult result, string? snippet = null)
{
    public SearchResult Result { get; } = result;
    public string FileName => Result.FileName;
    public string Directory => Result.Directory;
    public string FullPath => Result.FullPath;
    public string Icon => Result.IsDirectory ? "📁" : "📄";
    public string? Snippet { get; } = snippet;
}
