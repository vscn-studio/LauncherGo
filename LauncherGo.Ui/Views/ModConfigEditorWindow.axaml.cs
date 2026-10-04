using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Interactivity;
using AvaloniaEdit.Highlighting;
using LauncherGo.Abstractions.Services.I18n;
using LauncherGo.Ui;

namespace LauncherGo.Ui.Views;

public partial class ModConfigEditorWindow : Window
{
    private string _filePath = string.Empty;
    private Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private bool _isChinese;
    private bool _isLoading;
    private bool _isDirty;

    public ModConfigEditorWindow()
    {
        InitializeComponent();
    }

    public ModConfigEditorWindow(string filePath, bool isChinese)
        : this()
    {
        _filePath = Path.GetFullPath(filePath);
        _isChinese = isChinese;
        _encoding = DetectTextEncoding(_filePath);

        FileNameTextBlock.Text = Path.GetFileName(_filePath);
        ReloadButton.Content = T("重新加载", "Reload");
        ValidateButton.Content = T("检查", "Validate");
        SaveButton.Content = T("保存", "Save");
        CloseButton.Content = T("关闭", "Close");
        Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(_filePath));
        Editor.TextArea.SelectionForeground = Brushes.White;
        LoadFile();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _isDirty = true;
        UpdateWindowTitle();
    }

    private void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        LoadFile();
    }

    private void OnValidateClick(object? sender, RoutedEventArgs e)
    {
        ValidateEditorContent();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_filePath))
        {
            return;
        }

        var content = Editor.Text ?? string.Empty;
        if (!ValidateEditorContent())
        {
            return;
        }

        try
        {
            WriteFileAtomically(_filePath, content, _encoding);
            _isDirty = false;
            SetStatus(T("配置已保存。", "Configuration saved."));
            UpdateWindowTitle();
        }
        catch (Exception ex)
        {
            SetStatus(T($"保存失败：{ex.Message}", $"Save failed: {ex.Message}"), isError: true);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LoadFile()
    {
        try
        {
            _isLoading = true;
            _encoding = DetectTextEncoding(_filePath);
            Editor.Text = File.ReadAllText(_filePath, _encoding);
            _isDirty = false;
            SetStatus(T("已加载。", "Loaded."));
            UpdateWindowTitle();
        }
        catch (Exception ex)
        {
            SetStatus(T($"读取失败：{ex.Message}", $"Load failed: {ex.Message}"), isError: true);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private bool ValidateEditorContent()
    {
        var content = Editor.Text ?? string.Empty;
        var extension = Path.GetExtension(_filePath).ToLowerInvariant();

        try
        {
            switch (extension)
            {
                case ".json":
                    using (JsonDocument.Parse(content, new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow
                    }))
                    {
                    }

                    SetStatus(T("JSON 格式正确。", "JSON syntax is valid."));
                    return true;
                case ".jsonc":
                    using (JsonDocument.Parse(content, new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    }))
                    {
                    }

                    SetStatus(T("JSON 格式正确。", "JSON syntax is valid."));
                    return true;
                case ".xml":
                    XDocument.Parse(content, LoadOptions.PreserveWhitespace);
                    SetStatus(T("XML 格式正确。", "XML syntax is valid."));
                    return true;
                default:
                    SetStatus(T("此文件类型不提供语法检查。", "Syntax validation is unavailable for this file type."));
                    return true;
            }
        }
        catch (JsonException ex)
        {
            var line = checked((int)Math.Max(0, ex.LineNumber ?? 0)) + 1;
            var column = GetJsonColumn(content, line, ex.BytePositionInLine ?? 0);
            ReportSyntaxError("JSON", line, column);
            return false;
        }
        catch (XmlException ex)
        {
            ReportSyntaxError("XML", Math.Max(1, ex.LineNumber), Math.Max(1, ex.LinePosition));
            return false;
        }
    }

    private void ReportSyntaxError(string format, int line, int column)
    {
        SetStatus(
            T($"{format} 语法错误：第 {line} 行，第 {column} 列。", $"{format} syntax error at line {line}, column {column}."),
            isError: true);
        FocusErrorLocation(line, column);
    }

    private void FocusErrorLocation(int line, int column)
    {
        if (Editor.Document is null)
        {
            return;
        }

        line = Math.Clamp(line, 1, Editor.Document.LineCount);
        var documentLine = Editor.Document.GetLineByNumber(line);
        var offsetInLine = Math.Clamp(column - 1, 0, documentLine.Length);
        var offset = documentLine.Offset + offsetInLine;
        var length = offset < Editor.Document.TextLength ? 1 : 0;

        Editor.Select(offset, length);
        Editor.CaretOffset = offset;
        Editor.Focus();
        Editor.ScrollTo(line, offsetInLine + 1);
    }

    private static int GetJsonColumn(string content, int oneBasedLine, long bytePosition)
    {
        var lineStart = 0;
        for (var line = 1; line < oneBasedLine && lineStart < content.Length; line++)
        {
            var lineEnd = content.IndexOf('\n', lineStart);
            lineStart = lineEnd < 0 ? content.Length : lineEnd + 1;
        }

        var lineEndOffset = content.IndexOfAny(['\r', '\n'], lineStart);
        if (lineEndOffset < 0)
        {
            lineEndOffset = content.Length;
        }

        var targetBytePosition = Math.Max(0, bytePosition);
        var offset = lineStart;
        var consumedBytes = 0L;
        while (offset < lineEndOffset && consumedBytes < targetBytePosition)
        {
            var characterLength = char.IsHighSurrogate(content[offset]) &&
                                  offset + 1 < lineEndOffset &&
                                  char.IsLowSurrogate(content[offset + 1])
                ? 2
                : 1;
            var characterBytes = Encoding.UTF8.GetByteCount(content.AsSpan(offset, characterLength));
            if (consumedBytes + characterBytes > targetBytePosition)
            {
                break;
            }

            consumedBytes += characterBytes;
            offset += characterLength;
        }

        return offset - lineStart + 1;
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Classes.Set("Error", isError);
        ToolTip.SetTip(StatusTextBlock, message);
    }

    private void UpdateWindowTitle()
    {
        var name = string.IsNullOrWhiteSpace(_filePath) ? T("模组配置", "Mod Configuration") : Path.GetFileName(_filePath);
        Title = _isDirty
            ? $"* {name} - {T("模组配置", "Mod Configuration")}"
            : $"{name} - {T("模组配置", "Mod Configuration")}";
    }

    private string T(string zh, string en)
    {
        try
        {
            return ServiceLocator.GetRequiredService<ILocalizationService>().Resolve(zh, en);
        }
        catch (InvalidOperationException)
        {
            return _isChinese ? zh : en;
        }
    }

    private static Encoding DetectTextEncoding(string filePath)
    {
        Span<byte> bytes = stackalloc byte[4];
        using var stream = File.OpenRead(filePath);
        var count = stream.Read(bytes);

        if (count >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        }

        if (count >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        }

        if (count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (count >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        if (count >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static void WriteFileAtomically(string filePath, string content, Encoding encoding)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Configuration directory is unavailable.");
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, content, encoding);
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
