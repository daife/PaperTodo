using System.IO;
using System.Windows;
using PaperTodo;

internal static partial class ClipboardImageChecks
{
    public static void RunFileDropChecks(Action<string, Action> check)
    {
        check("File and folder drops create links with exact destinations and one undo", () =>
        {
            using var fixture = new FixtureDirectory();
            var file = Path.Combine(fixture.Path, "报告 [v1] (#2) & _draft_!.txt");
            var folder = Path.Combine(fixture.Path, "folder.png");
            File.WriteAllText(file, "contents");
            Directory.CreateDirectory(folder);
            var box = new MarkdownTextBox { Text = "before\nafter" };
            box.Select(7, 5);
            box.Document.UndoStack.ClearAll();
            var data = FileDrop(file, folder);
            // Shell data can advertise text too. FileDrop must win over that text.
            data.SetText("unrelated shell text");
            Require(box.TryInsertFilesFromDataObject(data), "File drop was not handled.");
            var inserted = box.Text;
            var links = MarkdownSemanticSnapshot.Parse(inserted).Links;
            Require(links.Count == 2, "Special filename punctuation must not break Markdown links.");
            Require(new Uri(links[0].Url).LocalPath == file && new Uri(links[1].Url).LocalPath == folder,
                "Links must resolve to the original file and folder, in order.");
            Require(!inserted.Contains("unrelated shell text"), "File drops must not insert the shell text payload.");
            Require(!MarkdownImageReferences.CollectImageIds(inserted).Any(),
                "A folder with an image extension must be linked, not imported.");
            box.Undo();
            Require(box.Text == "before\nafter" && !box.CanUndo, "One undo restores the selection and entire batch.");
            box.Redo();
            Require(box.Text == inserted, "Redo restores the same destinations.");
            Require(File.ReadAllText(file) == "contents" && Directory.Exists(folder), "Drop preserves source files.");
        });

        check("Mixed image and file drops retain order, image storage and one undo", () =>
        {
            using var fixture = new FixtureDirectory();
            var file = Path.Combine(fixture.Path, "first.txt");
            var image = Path.Combine(fixture.Path, "middle.png");
            var folder = Path.Combine(fixture.Path, "last");
            File.WriteAllText(file, "contents");
            File.WriteAllBytes(image, EncodePng(MixedPixels));
            Directory.CreateDirectory(folder);
            using var store = new NoteImageStore(Path.Combine(fixture.Path, "images.lmdb"));
            store.Load();
            var box = new MarkdownTextBox { Text = "before" };
            box.ConfigureNoteImages("file-drop-note", store);
            box.CaretIndex = box.Text.Length;
            box.Select(box.Text.Length, 0);
            box.Document.UndoStack.ClearAll();
            Require(box.TryInsertFilesFromDataObject(FileDrop(file, image, folder)), "Mixed drop was not handled.");
            var inserted = box.Text;
            var imageId = MarkdownImageReferences.CollectImageIds(inserted).Single();
            AssertStoredPixels(store, imageId, MixedPixels);
            Require(inserted.IndexOf("first", StringComparison.Ordinal) < inserted.IndexOf("i:", StringComparison.Ordinal) &&
                inserted.IndexOf("i:", StringComparison.Ordinal) < inserted.IndexOf("last", StringComparison.Ordinal),
                "Mixed targets must keep their original order.");
            box.Undo();
            Require(box.Text == "before" && !box.CanUndo, "One undo must remove the complete mixed drop.");
            box.Redo();
            Require(box.Text == inserted, "Redo must reuse the imported image reference.");

            box.Text = "before";
            box.Select(box.Text.Length, 0);
            box.MaxLength = box.Text.Length + 1;
            box.Document.UndoStack.ClearAll();
            var rejected = false;
            box.PasteRejected += () => rejected = true;
            Require(box.TryInsertFilesFromDataObject(FileDrop(file, image)), "Oversized drop should be consumed.");
            Require(rejected && box.Text == "before" && !box.CanUndo,
                "Length rejection must leave the existing text and undo history intact.");
            store.SetViewportProtectedBitmapIds("file-drop-note", Array.Empty<string>());
        });

        check("Read-only and missing-file drops leave note text intact", () =>
        {
            using var fixture = new FixtureDirectory();
            var missing = Path.Combine(fixture.Path, "missing.txt");
            var box = new MarkdownTextBox { Text = "before", IsReadOnly = true };
            Require(!box.TryInsertFilesFromDataObject(FileDrop(missing)), "Read-only note must reject file insertion.");
            box.IsReadOnly = false;
            try
            {
                box.TryInsertFilesFromDataObject(FileDrop(fixture.Path, missing));
                throw new InvalidOperationException("A missing file must fail the whole batch.");
            }
            catch (IOException)
            {
                Require(box.Text == "before", "Failed drops must not insert a partial batch.");
            }
            var text = new DataObject(DataFormats.UnicodeText, "plain text");
            Require(!box.TryInsertFilesFromDataObject(text), "Plain text must stay on the native text drop path.");
        });
    }

    private static DataObject FileDrop(params string[] paths)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        return data;
    }
}
