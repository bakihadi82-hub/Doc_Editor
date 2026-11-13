using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using PdfStudio.App.ViewModels;
using MarkdigBlock = Markdig.Syntax.Block;
using MarkdigInline = Markdig.Syntax.Inlines.Inline;

namespace PdfStudio.App.Views.Editors;

public partial class MarkdownTextEditorView : UserControl
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private MarkdownTextWorkspaceViewModel? _vm;
    private bool _suppressEditorSync;
    private readonly SearchMatchTransformer _searchTransformer = new();

    public MarkdownTextEditorView()
    {
        InitializeComponent();
        CodeEditor.TextChanged += OnEditorTextChanged;
        CodeEditor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        CodeEditor.TextArea.TextView.LineTransformers.Add(_searchTransformer);
        ActualThemeVariantChanged += OnThemeVariantChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Application.Current is not null)
        {
            Application.Current.ResourcesChanged += OnResourcesChanged;
        }
        RefreshMarkdownPreview();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Application.Current is not null)
        {
            Application.Current.ResourcesChanged -= OnResourcesChanged;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        if (_vm is not null && _vm.IsMarkdownFile && _vm.ShowPreviewPane)
        {
            RefreshMarkdownPreview();
        }
    }

    private void OnResourcesChanged(object? sender, ResourcesChangedEventArgs e)
    {
        if (_vm is not null && _vm.IsMarkdownFile && _vm.ShowPreviewPane)
        {
            RefreshMarkdownPreview();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.RequestSelectRange -= OnRequestSelectRange;
            _vm.RequestRefreshSearchHighlights -= OnRefreshSearchHighlights;
            _vm.RequestMarkdownPreviewRefresh -= RefreshMarkdownPreview;
            _vm.RequestCanUndo = null;
            _vm.RequestCanRedo = null;
            _vm.RequestEditorUndo = null;
            _vm.RequestEditorRedo = null;
        }

        base.OnDataContextChanged(e);

        _vm = DataContext as MarkdownTextWorkspaceViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.RequestSelectRange += OnRequestSelectRange;
            _vm.RequestRefreshSearchHighlights += OnRefreshSearchHighlights;
            _vm.RequestMarkdownPreviewRefresh += RefreshMarkdownPreview;

            _vm.RequestCanUndo = () => CodeEditor.CanUndo;
            _vm.RequestCanRedo = () => CodeEditor.CanRedo;
            _vm.RequestEditorUndo = () => CodeEditor.Undo();
            _vm.RequestEditorRedo = () => CodeEditor.Redo();

            SyncFromViewModel();
            ApplySyntaxHighlighting();
            UpdateColumnLayout();
            RefreshMarkdownPreview();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        switch (e.PropertyName)
        {
            case nameof(MarkdownTextWorkspaceViewModel.TextContent):
                if (!string.Equals(CodeEditor.Text, _vm.TextContent, StringComparison.Ordinal))
                {
                    SyncFromViewModel();
                }
                break;
            case nameof(MarkdownTextWorkspaceViewModel.ViewMode):
            case nameof(MarkdownTextWorkspaceViewModel.IsMarkdownFile):
                UpdateColumnLayout();
                RefreshMarkdownPreview();
                break;
        }
    }

    private void SyncFromViewModel()
    {
        if (_vm is null) return;
        _suppressEditorSync = true;
        try
        {
            CodeEditor.Text = _vm.TextContent;
        }
        finally
        {
            _suppressEditorSync = false;
        }
    }

    private void ApplySyntaxHighlighting()
    {
        if (_vm is null) return;
        string ext = _vm.SyntaxExtension;
        var def = HighlightingManager.Instance.GetDefinitionByExtension(ext);
        if (def is null && ext == ".md")
        {
            def = HighlightingManager.Instance.GetDefinition("MarkDown");
        }
        CodeEditor.SyntaxHighlighting = def;
    }

    private void UpdateColumnLayout()
    {
        if (_vm is null) return;
        if (_vm.IsMarkdownFile && _vm.ViewMode == MarkdownViewMode.SplitPreview)
        {
            Grid.SetColumn(EditorContainer, 0);
            Grid.SetColumnSpan(EditorContainer, 1);
            Grid.SetColumn(PreviewScroll, 2);
            Grid.SetColumnSpan(PreviewScroll, 1);
        }
        else if (_vm.IsMarkdownFile && _vm.ViewMode == MarkdownViewMode.CleanReader)
        {
            Grid.SetColumn(PreviewScroll, 0);
            Grid.SetColumnSpan(PreviewScroll, 3);
        }
        else
        {
            Grid.SetColumn(EditorContainer, 0);
            Grid.SetColumnSpan(EditorContainer, 3);
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorSync || _vm is null) return;
        _vm.UpdateTextFromEditor(CodeEditor.Text);
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        var loc = CodeEditor.TextArea.Caret.Location;
        _vm.CaretLine = loc.Line;
        _vm.CaretColumn = loc.Column;
    }

    private void OnRequestSelectRange((int Offset, int Length) range)
    {
        if (range.Offset < 0 || range.Offset + range.Length > CodeEditor.Document.TextLength) return;
        CodeEditor.Select(range.Offset, range.Length);
        var loc = CodeEditor.Document.GetLocation(range.Offset);
        CodeEditor.ScrollTo(loc.Line, loc.Column);
    }

    private void OnRefreshSearchHighlights()
    {
        if (_vm is null) return;
        _searchTransformer.Update(_vm.SearchMatches, _vm.ActiveMatchIndex);
        CodeEditor.TextArea.TextView.Redraw();
    }

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Enter)
        {
            _vm.RunSearch(FindBox.Text ?? string.Empty);
            e.Handled = true;
        }
    }

    private void OnReplaceClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        string q = FindBox.Text ?? string.Empty;
        if (_vm.SearchMatches.Count == 0 && !string.IsNullOrEmpty(q))
        {
            _vm.RunSearch(q);
        }
        _vm.ReplaceCurrentMatch(q);
    }

    private void OnReplaceAllClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ReplaceAllMatches(FindBox.Text ?? string.Empty);
    }

    private void RefreshMarkdownPreview()
    {
        PreviewHost.Children.Clear();
        if (_vm is null || !_vm.IsMarkdownFile || !_vm.ShowPreviewPane) return;

        var doc = Markdown.Parse(_vm.TextContent ?? string.Empty, Pipeline);
        foreach (var block in doc)
        {
            var ctrl = RenderMarkdownBlock(block);
            if (ctrl is not null)
            {
                PreviewHost.Children.Add(ctrl);
            }
        }
    }

    private IBrush ResolveBrush(string key, IBrush fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var res) && res is IBrush b)
        {
            return b;
        }
        return fallback;
    }

    private Control? RenderMarkdownBlock(MarkdigBlock block)
    {
        var textPrimary = ResolveBrush("TextPrimaryBrush", Brushes.White);
        var textSecondary = ResolveBrush("TextSecondaryBrush", Brushes.Gray);
        var hairline = ResolveBrush("HairlineBorderBrush", Brushes.DimGray);
        var panelBg = ResolveBrush("PanelBackgroundBrush", Brushes.Black);
        var accent = ResolveBrush("AccentBrush", Brushes.CornflowerBlue);

        switch (block)
        {
            case HeadingBlock heading:
            {
                double size = heading.Level switch
                {
                    1 => 26,
                    2 => 21,
                    3 => 17,
                    4 => 15,
                    _ => 13.5
                };
                var tb = CreateInlineTextBlock(heading.Inline, size, FontWeight.Bold, textPrimary);
                if (heading.Level <= 2)
                {
                    var sp = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 2) };
                    sp.Children.Add(tb);
                    sp.Children.Add(new Border { Height = 1, Background = hairline });
                    return sp;
                }
                tb.Margin = new Thickness(0, 8, 0, 2);
                return tb;
            }
            case ParagraphBlock para:
                return CreateInlineTextBlock(para.Inline, 14, FontWeight.Normal, textPrimary);

            case QuoteBlock quote:
            {
                var inner = new StackPanel { Spacing = 6 };
                foreach (var child in quote)
                {
                    var ctrl = RenderMarkdownBlock(child);
                    if (ctrl is not null) inner.Children.Add(ctrl);
                }
                return new Border
                {
                    BorderBrush = accent,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 4, 4, 4),
                    Child = inner
                };
            }
            case CodeBlock code:
            {
                string text = code.Lines.ToString();
                return new Border
                {
                    Background = panelBg,
                    BorderBrush = hairline,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14, 10),
                    Child = new SelectableTextBlock
                    {
                        Text = text,
                        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New, monospace"),
                        FontSize = 12.5,
                        Foreground = textPrimary
                    }
                };
            }
            case ListBlock list:
            {
                var stack = new StackPanel { Spacing = 4, Margin = new Thickness(8, 2, 0, 2) };
                int num = 1;
                foreach (var itemObj in list)
                {
                    if (itemObj is not ListItemBlock item) continue;
                    string marker = list.IsOrdered ? $"{num++}." : "•";
                    var row = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("22,*")
                    };
                    var markerTb = new TextBlock
                    {
                        Text = marker,
                        FontSize = 14,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = accent,
                        VerticalAlignment = VerticalAlignment.Top
                    };
                    Grid.SetColumn(markerTb, 0);
                    row.Children.Add(markerTb);

                    var contentStack = new StackPanel { Spacing = 4 };
                    foreach (var sub in item)
                    {
                        var subCtrl = RenderMarkdownBlock(sub);
                        if (subCtrl is not null) contentStack.Children.Add(subCtrl);
                    }
                    Grid.SetColumn(contentStack, 1);
                    row.Children.Add(contentStack);
                    stack.Children.Add(row);
                }
                return stack;
            }
            case ThematicBreakBlock:
                return new Border { Height = 1, Background = hairline, Margin = new Thickness(0, 8) };

            case Table table:
            {
                var tableStack = new StackPanel { Spacing = 0 };
                foreach (var rowObj in table)
                {
                    if (rowObj is not TableRow tr) continue;
                    var rowGrid = new Grid();
                    int colIdx = 0;
                    foreach (var cellObj in tr)
                    {
                        if (cellObj is not TableCell cell) continue;
                        rowGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                        var cellStack = new StackPanel { Spacing = 2 };
                        foreach (var sub in cell)
                        {
                            var ctrl = RenderMarkdownBlock(sub);
                            if (ctrl is not null) cellStack.Children.Add(ctrl);
                        }
                        var cellBorder = new Border
                        {
                            BorderBrush = hairline,
                            BorderThickness = new Thickness(0.5),
                            Background = tr.IsHeader ? panelBg : Brushes.Transparent,
                            Padding = new Thickness(8, 5),
                            Child = cellStack
                        };
                        Grid.SetColumn(cellBorder, colIdx++);
                        rowGrid.Children.Add(cellBorder);
                    }
                    tableStack.Children.Add(rowGrid);
                }
                return new Border
                {
                    BorderBrush = hairline,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Child = tableStack
                };
            }
            default:
                return null;
        }
    }

    private SelectableTextBlock CreateInlineTextBlock(ContainerInline? container, double fontSize, FontWeight baseWeight, IBrush foreground)
    {
        var tb = new SelectableTextBlock
        {
            FontSize = fontSize,
            FontWeight = baseWeight,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = fontSize * 1.45
        };

        if (container is null) return tb;

        var inlines = tb.Inlines ??= [];
        AppendInlines(inlines, container, isBold: baseWeight == FontWeight.Bold, isItalic: false);
        return tb;
    }

    private void AppendInlines(InlineCollection target, ContainerInline container, bool isBold, bool isItalic)
    {
        foreach (MarkdigInline inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    target.Add(new Run(lit.Content.ToString())
                    {
                        FontWeight = isBold ? FontWeight.Bold : FontWeight.Normal,
                        FontStyle = isItalic ? FontStyle.Italic : FontStyle.Normal
                    });
                    break;
                case CodeInline code:
                    target.Add(new Run($" {code.Content} ")
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New, monospace"),
                        Background = ResolveBrush("PanelBackgroundBrush", Brushes.Black)
                    });
                    break;
                case EmphasisInline emph:
                    bool nextBold = isBold || emph.DelimiterCount >= 2;
                    bool nextItalic = isItalic || emph.DelimiterCount == 1;
                    AppendInlines(target, emph, nextBold, nextItalic);
                    break;
                case LinkInline link:
                    AppendInlines(target, link, isBold, isItalic);
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case ContainerInline sub:
                    AppendInlines(target, sub, isBold, isItalic);
                    break;
            }
        }
    }

    private sealed class SearchMatchTransformer : DocumentColorizingTransformer
    {
        private IReadOnlyList<(int Offset, int Length)> _matches = [];
        private int _activeIdx = -1;

        public void Update(IReadOnlyList<(int Offset, int Length)> matches, int activeIdx)
        {
            _matches = matches;
            _activeIdx = activeIdx;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (_matches.Count == 0) return;
            int lineStart = line.Offset;
            int lineEnd = line.EndOffset;

            for (int i = 0; i < _matches.Count; i++)
            {
                var (offset, length) = _matches[i];
                int matchEnd = offset + length;
                if (matchEnd <= lineStart || offset >= lineEnd) continue;

                int start = Math.Max(lineStart, offset);
                int end = Math.Min(lineEnd, matchEnd);
                bool active = i == _activeIdx;
                ChangeLinePart(start, end, el =>
                {
                    el.TextRunProperties.SetBackgroundBrush(active
                        ? new SolidColorBrush(Color.FromArgb(180, 249, 115, 22))
                        : new SolidColorBrush(Color.FromArgb(100, 250, 204, 21)));
                });
            }
        }
    }
}
