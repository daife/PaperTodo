using System.Reflection;
using System.Text.Json;
using PaperTodo;

internal static partial class Program
{
    private static void CheckTodoMultiLinks()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var legacy = JsonSerializer.Deserialize<PaperItem>("""{"linkedNoteId":"note"}""", options)!;
        Assert(legacy.GetQuickLaunchLinks().Single().PaperId == "note", "Legacy paper links must load.");
        legacy.AddQuickLaunchLink(new(null, @"C:\Work\a.txt", false));
        legacy.AddQuickLaunchLink(new(null, @"c:\work\A.txt", false));
        legacy.AddQuickLaunchLink(new("note", null));
        legacy.AddQuickLaunchLink(new(null, @"C:\Work", true));
        Assert(legacy.GetQuickLaunchLinks().Count() == 3, "Links must append and deduplicate by target.");
        var snapshotType = typeof(PaperItem).Assembly.GetType("PaperTodo.PaperCommandService")!
            .GetNestedType("PaperItemSnapshot", BindingFlags.NonPublic)!;
        var snapshot = snapshotType.GetMethod("Capture")!.Invoke(null, [legacy]);
        var beforeRollback = legacy.GetQuickLaunchLinks().ToArray();
        legacy.LinkPaper("temporary");
        snapshotType.GetMethod("Restore")!.Invoke(snapshot, null);
        Assert(legacy.GetQuickLaunchLinks().SequenceEqual(beforeRollback),
            "External mutation rollback must restore every target in order.");
        var roundTrip = JsonSerializer.Deserialize<PaperItem>(JsonSerializer.Serialize(legacy, options), options)!;
        Assert(roundTrip.GetQuickLaunchLinks().SequenceEqual(legacy.GetQuickLaunchLinks()), "Mixed links must round trip in order.");
        roundTrip.RemoveQuickLaunchLink(new("note", null));
        Assert(roundTrip.LinkedPath == @"C:\Work\a.txt" && roundTrip.GetQuickLaunchLinks().Count() == 2,
            "Removing the first target must promote the next without losing the others.");
        Assert(legacy.GetQuickLaunchLinks().Count() == 3, "Round-tripped links must be independent.");

        var oldMixed = JsonSerializer.Deserialize<PaperItem>("""{"linkedNoteId":"deleted-note","linkedPath":"C:\\Work"}""", options)!;
        var state = new AppState { EnableTodoPaperLinks = true };
        legacy.AddQuickLaunchLink(new("deleted-note", null));
        legacy.AddQuickLaunchLink(new("todo", null));
        state.Papers.Add(new PaperData { Id = "todo", Type = PaperTypes.Todo, Items = [legacy, oldMixed] });
        state.Papers.Add(new PaperData { Id = "note", Type = PaperTypes.Note });
        typeof(StateStore).GetMethod("NormalizeAfterLoad", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [state]);
        Assert(legacy.LinkedPaperIds.SequenceEqual(new[] { "note" }) && legacy.GetQuickLaunchLinks().Count() == 3,
            "Load normalization must remove missing/self references while preserving all valid paths and papers.");
        Assert(oldMixed.LinkedPath == @"C:\Work" && !oldMixed.LinkedPaperIds.Any(),
            "An invalid legacy paper reference must not discard an existing path.");
        var saved = new StateStore().SerializeState(state);
        var restored = JsonSerializer.Deserialize<AppState>(saved, options)!;
        Assert(restored.Papers[0].Items[0].GetQuickLaunchLinks().SequenceEqual(legacy.GetQuickLaunchLinks()),
            "StateStore serialization must preserve the full link collection.");
        legacy.LinkPaper("replacement");
        Assert(legacy.GetQuickLaunchLinks().Count() == 1 && legacy.LinkedPaperId == "replacement",
            "Legacy scalar replacement must retain its existing semantics.");
        legacy.ClearQuickLaunch();
        Assert(!legacy.GetQuickLaunchLinks().Any(), "Clearing links must clear the complete collection.");
    }
}
