using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HighshoreCairn.Converters;
using HighshoreCairn.Services;
using HighshoreCairn.ViewModels;

namespace HighshoreCairn.Views;

/// <summary>
/// Editor of a rich text (.rtf) document. The ViewModel holds the RTF source; this control loads it into
/// a RichTextBox and writes it back a moment after every change. Reading and writing RTF is done by WPF.
/// </summary>
public partial class RichDocEditor : UserControl
{
    private static readonly string[] Fonts =
    {
        "Segoe UI", "Arial", "Calibri", "Cambria", "Georgia", "Times New Roman", "Verdana", "Tahoma", "Trebuchet MS", "Consolas", "Courier New"
    };

    private static readonly double[] Sizes = { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 40, 48, 64 };

    private static readonly string[] TextPalette =
    {
        "#000000", "#5F6B7A", "#9AA5B1", "#E5484D", "#F76B15", "#F2A541", "#C29B00", "#7CB518",
        "#30A46C", "#12A594", "#1F6F8B", "#0091FF", "#5B5BD6", "#8E4EC6", "#D6409F", "#16324F"
    };

    private static readonly string[] HighlightPalette =
    {
        "#FFE08A", "#FFF7CC", "#FDECEC", "#FDF1DC", "#E6F4D7", "#DDF3EA", "#E3F0F4", "#E1E9FF",
        "#EFE5FA", "#FBE4F1", "#ECEFF2", "#FFD0D0", "#C6F0C2", "#BDE3FF", "#FFD8A8", "#D9DEE3"
    };

    private DocumentViewModel? _vm;
    private readonly DispatcherTimer _syncTimer;
    private bool _loading;       // the page is being filled from the file: its changes are not edits
    private bool _dirty;         // the page has edits that are not in the RTF text of the ViewModel yet
    private bool _syncingBar;    // the toolbar is being updated from the selection
    private string? _loadedRtf;  // what the page currently shows (avoids reloading our own text)

    public RichDocEditor()
    {
        InitializeComponent();
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _syncTimer.Tick += (_, _) => { _syncTimer.Stop(); Push(); };

        FontBox.ItemsSource = Fonts;
        SizeBox.ItemsSource = Sizes;
        TextColors.ItemsSource = TextPalette;
        HighlightColors.ItemsSource = HighlightPalette;

        DataContextChanged += (_, _) => Attach(DataContext as DocumentViewModel);
        Loaded += (_, _) => { if (_vm is null) Attach(DataContext as DocumentViewModel); };
        Unloaded += (_, _) => { _syncTimer.Stop(); Attach(null); };
    }

    // ============================================================================ life cycle

    private void Attach(DocumentViewModel? vm)
    {
        if (vm is { IsRich: false }) vm = null;   // this editor only shows rich text documents
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm != null)
        {
            Push();
            _vm.FlushRequested -= OnFlushRequested;
            _vm.Reloaded -= OnReloaded;
        }
        _vm = vm;
        _dirty = false;
        _loadedRtf = null;
        if (_vm is null) return;

        _vm.FlushRequested += OnFlushRequested;
        _vm.Reloaded += OnReloaded;
        LoadPage();
    }

    private void OnFlushRequested()
    {
        _syncTimer.Stop();
        Push();
    }

    private void OnReloaded()
    {
        _dirty = false;
        LoadPage();
    }

    /// <summary>Fills the page from the RTF text of the ViewModel.</summary>
    private void LoadPage()
    {
        if (_vm is null || _vm.Text == _loadedRtf) return;
        _loading = true;
        try
        {
            var document = new FlowDocument { PagePadding = new Thickness(0) };
            var rtf = _vm.Text;
            var readable = true;
            if (!string.IsNullOrWhiteSpace(rtf))
            {
                try
                {
                    using var stream = new MemoryStream(RtfText.ToBytes(rtf));
                    new TextRange(document.ContentStart, document.ContentEnd).Load(stream, DataFormats.Rtf);
                }
                catch
                {
                    // Not valid RTF (edited by hand, damaged...): show its text rather than nothing, but
                    // read-only, so that the file is never replaced by this approximation.
                    readable = false;
                    document.Blocks.Clear();
                    document.Blocks.Add(new Paragraph(new Run(
                        "(This file could not be read as rich text: its text is shown below and cannot be edited here.)")) { Foreground = Brushes.Gray });
                    document.Blocks.Add(new Paragraph(new Run(RtfText.ToPlain(rtf))));
                }
            }
            Box.Document = document;
            Box.IsReadOnly = !readable;
            _loadedRtf = _vm.Text;
        }
        finally
        {
            _loading = false;
        }
        _dirty = false;
        Box.IsUndoEnabled = false;   // the load is not something to undo
        Box.IsUndoEnabled = true;
    }

    private void Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _vm is null) return;
        _dirty = true;
        _syncTimer.Stop();
        _syncTimer.Start();
    }

    /// <summary>Writes the edits of the page into the RTF text of the ViewModel (which marks the document as modified).</summary>
    private void Push()
    {
        if (!_dirty || _vm is null) return;
        _dirty = false;
        try
        {
            using var stream = new MemoryStream();
            new TextRange(Box.Document.ContentStart, Box.Document.ContentEnd).Save(stream, DataFormats.Rtf);
            var rtf = RtfText.FromBytes(stream.ToArray());
            _loadedRtf = rtf;
            if (rtf != _vm.Text) _vm.Text = rtf;
        }
        catch (Exception ex)
        {
            // Never replace the last good text because of an unexpected content: tell, and try again later.
            _dirty = true;
            _vm.Owner.Dialogs.Error("The rich text could not be read from the editor.\n\n" + ex.Message);
        }
    }

    // ============================================================================ toolbar

    private void OnTool(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { Tag: string action }) return;
        Box.Focus();
        try
        {
            switch (action)
            {
                case "bold": EditingCommands.ToggleBold.Execute(null, Box); break;
                case "italic": EditingCommands.ToggleItalic.Execute(null, Box); break;
                case "underline": EditingCommands.ToggleUnderline.Execute(null, Box); break;
                case "strike": ToggleStrike(); break;
                case "left": EditingCommands.AlignLeft.Execute(null, Box); break;
                case "center": EditingCommands.AlignCenter.Execute(null, Box); break;
                case "right": EditingCommands.AlignRight.Execute(null, Box); break;
                case "justify": EditingCommands.AlignJustify.Execute(null, Box); break;
                case "ul": EditingCommands.ToggleBullets.Execute(null, Box); break;
                case "ol": EditingCommands.ToggleNumbering.Execute(null, Box); break;
                case "indent": EditingCommands.IncreaseIndentation.Execute(null, Box); break;
                case "outdent": EditingCommands.DecreaseIndentation.Execute(null, Box); break;
                case "image": InsertImage(); break;
                case "undo": Box.Undo(); break;
                case "redo": Box.Redo(); break;
            }
        }
        catch (Exception ex)
        {
            _vm.Owner.Dialogs.Error("The formatting could not be applied here.\n\n" + ex.Message);
        }
    }

    /// <summary>Strikethrough has no editing command: it is added to (or removed from) the decorations of the selection.</summary>
    private void ToggleStrike()
    {
        var selection = Box.Selection;
        var current = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var has = current != null && current.Any(d => d.Location == TextDecorationLocation.Strikethrough);
        var result = new TextDecorationCollection();
        if (current != null)
            foreach (var decoration in current.Where(d => d.Location != TextDecorationLocation.Strikethrough)) result.Add(decoration);
        if (!has) result.Add(TextDecorations.Strikethrough[0]);
        selection.ApplyPropertyValue(Inline.TextDecorationsProperty, result);
    }

    private void Font_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBar || FontBox.SelectedItem is not string font) return;
        Box.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily(font));
        Box.Focus();
    }

    private void Size_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBar || SizeBox.SelectedItem is not double points) return;
        Box.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, points * 96.0 / 72.0);
        Box.Focus();
    }

    private void TextColor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TextColors.SelectedItem is not string hex) return;
        TextColors.SelectedItem = null;   // the same color can be picked again
        ApplyColor(TextElement.ForegroundProperty, HexToBrushConverter.Parse(hex));
        TextColorBar.Background = HexToBrushConverter.Parse(hex);
    }

    private void TextColor_Auto(object sender, RoutedEventArgs e) => ApplyColor(TextElement.ForegroundProperty, Brushes.Black);

    private void Highlight_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HighlightColors.SelectedItem is not string hex) return;
        HighlightColors.SelectedItem = null;
        ApplyColor(TextElement.BackgroundProperty, HexToBrushConverter.Parse(hex));
        HighlightBar.Background = HexToBrushConverter.Parse(hex);
    }

    private void Highlight_None(object sender, RoutedEventArgs e) => ApplyColor(TextElement.BackgroundProperty, null);

    private void ApplyColor(DependencyProperty property, Brush? brush)
    {
        try
        {
            Box.Selection.ApplyPropertyValue(property, brush);
        }
        catch (Exception ex)
        {
            _vm?.Owner.Dialogs.Error("The color could not be applied here.\n\n" + ex.Message);
        }
        Box.Focus();
    }

    /// <summary>Shows font and size of the selection in the toolbar (empty when the selection mixes several).</summary>
    private void Box_SelectionChanged(object sender, RoutedEventArgs e)
    {
        _syncingBar = true;
        try
        {
            var family = Box.Selection.GetPropertyValue(TextElement.FontFamilyProperty) as FontFamily;
            FontBox.SelectedItem = family is null ? null
                : Fonts.FirstOrDefault(f => family.Source.Split(',')[0].Trim().Equals(f, StringComparison.OrdinalIgnoreCase));
            SizeBox.SelectedItem = Box.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double pixels
                ? Sizes.Cast<double?>().FirstOrDefault(s => Math.Abs(s!.Value - pixels * 72.0 / 96.0) < 0.26)
                : null;
        }
        catch
        {
            // the toolbar is only a hint
        }
        finally
        {
            _syncingBar = false;
        }
    }

    private void InsertImage()
    {
        if (_vm is null) return;
        var file = _vm.Owner.Dialogs.PickFile("Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif");
        if (string.IsNullOrEmpty(file)) return;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(file, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            var width = Math.Min(bitmap.PixelWidth, 640);
            var image = new Image { Source = bitmap, Width = width, Height = width * (double)bitmap.PixelHeight / bitmap.PixelWidth, Stretch = Stretch.Uniform };
            if (!Box.Selection.IsEmpty) Box.Selection.Text = "";
            _ = new InlineUIContainer(image, Box.CaretPosition.GetInsertionPosition(LogicalDirection.Forward));
        }
        catch (Exception ex)
        {
            _vm.Owner.Dialogs.Error("The picture could not be inserted.\n\n" + ex.Message);
        }
    }

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _vm.SaveCommand.Execute(null);
            e.Handled = true;
        }
    }
}
