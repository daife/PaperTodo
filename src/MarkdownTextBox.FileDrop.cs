using System.IO;
using System.Text;
using System.Windows;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    internal static bool HasFileDropData(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop);
        }
        catch
        {
            return false;
        }
    }

    // File drops are separate from clipboard paste: ordinary files become links only on drop.
    public bool TryInsertFilesFromDataObject(IDataObject data)
    {
        if (IsReadOnly || !HasFileDropData(data) ||
            data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return false;
        }

        var imagePaths = new List<string>();
        var references = new List<string>();
        var imageIndexes = new List<int>();
        foreach (var rawPath in paths)
        {
            var path = Path.GetFullPath(rawPath);
            var isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path))
            {
                throw new IOException(Strings.Format("LinkedPathMissingMessage", path));
            }

            if (!isDirectory && NoteImageStore.IsSupportedImageFile(path))
            {
                imagePaths.Add(path);
                imageIndexes.Add(references.Count);
                // Match image insertion's preflight before any blob is written.
                references.Add(MarkdownImageReferences.CreateReference("99999999"));
            }
            else
            {
                var label = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                if (string.IsNullOrEmpty(label)) label = path;
                var escapedLabel = new StringBuilder();
                foreach (var c in label)
                {
                    if (c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~')
                        escapedLabel.Append('\\');
                    escapedLabel.Append(c);
                }
                // A file URI encodes spaces, brackets, # and other path characters without
                // changing the destination. Angle brackets also keep parentheses unambiguous.
                references.Add($"[{escapedLabel}](<{new Uri(path).AbsoluteUri}>)");
            }
        }

        if (imagePaths.Count == paths.Length)
            return TryInsertImagesFromDataObject(data);

        var target = CaptureDocumentReplacementTarget();
        string BuildInsertion() => BuildBlockInsertion(target.OriginalText, target.Start,
            string.Join(Environment.NewLine, references));
        var insertion = BuildInsertion();
        var candidate = ReplaceRange(target.OriginalText, target.Start, target.SelectionLength, insertion);
        if (insertion.Length > MaxSafePasteLength ||
            ContainsLineLongerThan(insertion, MaxSafePasteLineLength) ||
            (MaxLength > 0 && candidate.Length > TextLengthLimitFor(candidate)))
        {
            PasteRejected?.Invoke();
            return true;
        }

        if (imagePaths.Count > 0)
        {
            var imageStore = _imageStore
                ?? throw new InvalidOperationException(Strings.Get("ImageStoreUnavailable"));
            if (string.IsNullOrWhiteSpace(_noteId))
                throw new InvalidOperationException(Strings.Get("ImageImportInvalidNote"));
            var assets = imageStore.ImportImageFiles(_noteId, imagePaths);
            for (var index = 0; index < assets.Count; index++)
                references[imageIndexes[index]] = MarkdownImageReferences.CreateReference(assets[index].Id);
            insertion = BuildInsertion();
        }

        var caret = CommitDocumentReplacement(target, insertion);
        CaretIndex = caret;
        Select(caret, 0);
        Focus();
        QueuePostPasteRefresh();
        return true;
    }
}
